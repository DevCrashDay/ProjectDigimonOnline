using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DigimonNetplay.Shared;

// Runs the real relay server and 2-4 simulated game clients that use the same LockstepCore as the mod.
// Each client simulates a toy fighting game whose state depends on everyone's inputs and a per-frame seeded RNG,
// with random frame pacing, random scene-load times and real TCP in between. Every peer must produce the
// exact same state on every frame and switch scenes on the same frame.
internal static class Program
{
    private static int _failures;
    private static bool _webSocket; // connect test clients the way the game does behind Cloudflare

    private static int Main()
    {
        var server = new RelayServer();
        server.Start(0, System.Net.IPAddress.Loopback);
        int port = server.Port;
        Console.WriteLine("relay on port " + port);

        Run("1v1, delay 3, 3 rounds", () => Scenario(port, players: 2, delay: 3, segments: 3, framesPerSegment: 240, jitterMs: 6, injectDesync: false));
        Run("4 players, delay 2, 2 rounds", () => Scenario(port, players: 4, delay: 2, segments: 2, framesPerSegment: 180, jitterMs: 8, injectDesync: false));
        Run("1v1, delay 0 (every frame waits on the network)", () => Scenario(port, players: 2, delay: 0, segments: 2, framesPerSegment: 120, jitterMs: 3, injectDesync: false));
        Run("desync is detected and corrected toward the host", () => Scenario(port, players: 2, delay: 3, segments: 1, framesPerSegment: 300, jitterMs: 4, injectDesync: true));
        Run("a player leaving aborts the match for the others", () => LeaveScenario(port));
        Run("lobby: room codes, full rooms, bad codes, version check", () => LobbyScenario(port));
        Run("lobby browser: public, password and private rooms, in-match rooms", () => BrowserScenario(port));
        Run("P2P 1v1: direct links, delay 3, 2 rounds", () => Scenario(port, players: 2, delay: 3, segments: 2, framesPerSegment: 180, jitterMs: 6, injectDesync: false, p2p: new P2POptions()));
        Run("P2P 4 players with 25% packet loss", () => Scenario(port, players: 4, delay: 3, segments: 2, framesPerSegment: 180, jitterMs: 6, injectDesync: false, p2p: new P2POptions { DropRate = 0.25 }));
        Run("P2P link dies mid-match: falls back to the server", () => Scenario(port, players: 2, delay: 3, segments: 2, framesPerSegment: 240, jitterMs: 4, injectDesync: false, p2p: new P2POptions { CutAtFrame = 60 }));
        Run("P2P with one player who turned direct connections off", () => Scenario(port, players: 3, delay: 3, segments: 1, framesPerSegment: 180, jitterMs: 4, injectDesync: false, p2p: new P2POptions { UdpOff = new[] { false, false, true, false } }));
        _webSocket = true;
        Run("WebSocket: 1v1, delay 3, 2 rounds", () => Scenario(port, players: 2, delay: 3, segments: 2, framesPerSegment: 180, jitterMs: 6, injectDesync: false));
        Run("WebSocket: 4 players, delay 2", () => Scenario(port, players: 4, delay: 2, segments: 1, framesPerSegment: 180, jitterMs: 8, injectDesync: false));
        Run("WebSocket: lobby browser", () => BrowserScenario(port));
        _webSocket = false;
        Run("HTTP status page", () => StatusScenario(port));
        Run("server address parsing", AddressScenario);

        server.Stop();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : _failures + " TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        var sw = Stopwatch.StartNew();
        try { test(); Console.WriteLine("PASS  " + name + "  (" + sw.ElapsedMilliseconds + " ms)"); }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL  " + name + ": " + e.Message); }
    }

    private static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    // ------------------------------------------------------------------ helpers

    private static RelayClient Connect(int port, string name)
    {
        var c = new RelayClient();
        if (_webSocket) c.Connect("ws://127.0.0.1:" + port + "/", name);
        else c.Connect("127.0.0.1", port, name);
        return c;
    }

    private static T WaitFor<T>(RelayClient c, int timeoutMs = 3000) where T : RelayEvent
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            while (c.TryPoll(out var ev)) if (ev is T t) return t;
            c.Arrived.WaitOne(20);
        }
        throw new Exception("timed out waiting for " + typeof(T).Name);
    }

    private static (RelayClient[] clients, int[] slots) MakeRoom(int port, int players)
    {
        var clients = new RelayClient[players];
        var slots = new int[players];
        clients[0] = Connect(port, "P0");
        clients[0].CreateRoom(players);
        var joined = WaitFor<EvRoomJoined>(clients[0]);
        slots[0] = joined.MySlot;
        for (int i = 1; i < players; i++)
        {
            clients[i] = Connect(port, "P" + i);
            clients[i].JoinRoom(joined.Code);
            slots[i] = WaitFor<EvRoomJoined>(clients[i]).MySlot;
        }
        Thread.Sleep(100);
        foreach (var c in clients) while (c.TryPoll(out _)) { }
        return (clients, slots);
    }

    // ------------------------------------------------------------------ lockstep scenario

    private sealed class PeerResult
    {
        public readonly Dictionary<long, string> FrameHashes = new Dictionary<long, string>();
        public readonly List<(int seg, int frame)> Activations = new List<(int, int)>();
        public float[] FinalPos;
        public int Desyncs, ChecksPassed;
        public long Stalls;
        public int RelayedSeq; // P2P match messages that had to go through the server
        public string Error;
    }

    private sealed class P2POptions
    {
        public double DropRate;
        public bool[] UdpOff = new bool[4];
        public int CutAtFrame = -1; // both ends of the 0<->1 link go dead at this frame of round 1
    }

    private static void Scenario(int port, int players, int delay, int segments, int framesPerSegment, int jitterMs, bool injectDesync, P2POptions p2p = null)
    {
        var (clients, slots) = MakeRoom(port, players);
        P2PMux[] muxes = p2p == null ? null : SetUpP2P(clients, slots, p2p);
        var cfg = new MatchConfig
        {
            Seed = 12345, InputDelay = delay, Stage = 1,
            Slots = slots, Names = slots.Select(s => "P" + s).ToArray(),
            Picks = slots.Select(_ => new PlayerPick { Character = "Agumon" }).ToArray(),
        };
        var results = new PeerResult[players];
        var threads = new Thread[players];
        for (int i = 0; i < players; i++)
        {
            int me = i;
            results[me] = new PeerResult();
            threads[me] = new Thread(() =>
            {
                try { SimPeer(clients[me], cfg, me, segments, framesPerSegment, jitterMs, injectDesync && me == 1, results[me], muxes?[me], p2p, slots); }
                catch (Exception e) { results[me].Error = e.ToString(); }
            });
            threads[me].Start();
        }
        foreach (var t in threads) Check(t.Join(120000), "peer thread hung");
        foreach (var c in clients) c.Dispose();
        if (muxes != null) foreach (var m in muxes) m.Dispose();

        for (int i = 0; i < players; i++) Check(results[i].Error == null, "peer " + i + " failed: " + results[i].Error);
        for (int i = 1; i < players; i++)
            Check(results[i].Activations.SequenceEqual(results[0].Activations),
                "peers switched scenes on different frames: " + Fmt(results[0].Activations) + " vs " + Fmt(results[i].Activations));
        Check(results[0].Activations.Count == segments - 1, "expected " + (segments - 1) + " scene switches, got " + results[0].Activations.Count);

        if (!injectDesync)
        {
            for (int i = 1; i < players; i++)
            {
                Check(results[i].FrameHashes.Count == results[0].FrameHashes.Count, "different number of simulated frames");
                foreach (var kv in results[0].FrameHashes)
                    Check(results[i].FrameHashes.TryGetValue(kv.Key, out var h) && h == kv.Value,
                        "state diverged on peer " + i + " at segment " + (kv.Key >> 32) + " frame " + (int)kv.Key);
                Check(results[i].Desyncs == 0, "false desync reported on peer " + i);
                Check(results[i].ChecksPassed > 0, "no state checks ran on peer " + i);
            }
            Console.WriteLine("      " + results[0].FrameHashes.Count + " frames identical on " + players + " peers; scene switches at " + Fmt(results[0].Activations) +
                              "; stalled frames per peer: " + string.Join(",", results.Select(r => r.Stalls)) +
                              (p2p != null ? "; match messages via server per peer: " + string.Join(",", results.Select(r => r.RelayedSeq)) : ""));
            if (p2p != null && p2p.CutAtFrame < 0 && !p2p.UdpOff.Any(x => x))
                Check(results.All(r => r.RelayedSeq == 0), "P2P match fell back to the server although every link was up");
            if (p2p != null && (p2p.CutAtFrame >= 0 || p2p.UdpOff.Any(x => x)))
                Check(results.Any(r => r.RelayedSeq > 0), "expected the server fallback to carry messages");
        }
        else
        {
            Check(results[1].Desyncs >= 1, "injected desync was not detected");
            for (int p = 0; p < players; p++)
                Check(Math.Abs(results[1].FinalPos[p] - results[0].FinalPos[p]) < 0.05f,
                    "guest did not converge to host: " + results[1].FinalPos[p] + " vs " + results[0].FinalPos[p]);
            Console.WriteLine("      desync detected " + results[1].Desyncs + "x and corrected; final positions match within 0.05");
        }
    }

    private static string Fmt(List<(int seg, int frame)> a) => "[" + string.Join(" ", a.Select(x => x.seg + "@" + x.frame)) + "]";

    /// <summary>Mirrors MatchSession.OnFrame / LoadStage / OnSceneLoaded with a toy game instead of Unity.</summary>
    /// <summary>Like NetplayManager: every player announces itself, links get punched before the match starts.</summary>
    private static P2PMux[] SetUpP2P(RelayClient[] clients, int[] slots, P2POptions o)
    {
        int n = clients.Length;
        var muxes = new P2PMux[n];
        for (int i = 0; i < n; i++)
        {
            muxes[i] = new P2PMux(slots[i], !o.UdpOff[i], clients[i].Broadcast, clients[i].Arrived) { StunServers = new string[0], DropRate = o.DropRate };
            muxes[i].Announce();
        }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            for (int i = 0; i < n; i++)
                while (clients[i].TryPoll(out var ev))
                    if (ev is EvData d)
                        using (var r = GameMsg.Open(d.Payload, out byte t))
                            if (t == GameMsg.P2PHello) muxes[i].OnHello(d.FromSlot, r);
            bool allUp = true;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i != j && !o.UdpOff[i] && !o.UdpOff[j] && !muxes[i].IsDirect(slots[j])) allUp = false;
            if (allUp) break;
            Thread.Sleep(20);
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j && !o.UdpOff[i] && !o.UdpOff[j]) Check(muxes[i].IsDirect(slots[j]), "no direct link from " + i + " to " + j);
        return muxes;
    }

    private static void SimPeer(RelayClient client, MatchConfig cfg, int me, int segments, int framesPerSegment, int jitterMs, bool injectDesync, PeerResult res,
        P2PMux mux = null, P2POptions p2p = null, int[] slots = null)
    {
        if (mux != null) mux.BeginMatch(cfg.Seed, cfg.Slots);
        var core = new LockstepCore(cfg, me, me == 0, mux != null ? (Action<byte[]>)mux.Send : client.Broadcast);
        var rng = new Random(1000 + me);
        int n = cfg.PlayerCount;
        var pos = new float[n];
        var hp = new float[n];
        for (int p = 0; p < n; p++) { pos[p] = p; hp[p] = 1f; }
        var pads = new PadState[n];

        void Pump()
        {
            while (client.TryPoll(out var ev))
            {
                if (ev is EvData d)
                {
                    if (mux != null)
                    {
                        using (var r = GameMsg.Open(d.Payload, out byte t))
                        {
                            if (t == GameMsg.Seq) { res.RelayedSeq++; mux.OnRelayed(d.FromSlot, r); continue; }
                            if (t == GameMsg.P2PHello) { mux.OnHello(d.FromSlot, r); continue; }
                        }
                    }
                    core.Handle(d.FromSlot, d.Payload);
                }
                else if (ev is EvPeerLeft pl) core.OnPeerLeft(pl.Slot);
                else if (ev is EvDisconnected) core.Abort("disconnected", false);
            }
            if (mux != null) while (mux.TryReceive(out int from, out byte[] payload)) core.Handle(from, payload);
        }

        // Background "scene load" that reports Ready after a random time, like LoadRoutine.
        var readyQueueLocal = new ConcurrentQueue<int>();
        int loadingSeg = -1;
        bool activationRequested = false;
        void StartLoad(int seg)
        {
            loadingSeg = seg;
            activationRequested = false;
            int ms = rng.Next(30, 400);
            Task.Delay(ms).ContinueWith(_ => readyQueueLocal.Enqueue(seg));
        }

        StartLoad(0);
        while (true)
        {
            Pump();
            if (core.AbortReason != null) throw new Exception("aborted: " + core.AbortReason);
            while (readyQueueLocal.TryDequeue(out int rs)) core.LocalLoadReady(rs);

            // Not gating (first VS screen, or between scenes): activate as soon as everyone is ready.
            if (!core.Gating)
            {
                if (loadingSeg >= 0 && core.ShouldActivate(loadingSeg))
                {
                    Thread.Sleep(rng.Next(0, 120)); // scene activation takes a different time on each machine
                    core.BeginSegment(loadingSeg);
                    loadingSeg = -1;
                }
                else { client.Arrived.WaitOne(5); continue; }
            }

            // ---- one gated frame (MatchSession.OnFrame)
            if (loadingSeg >= 0 && !activationRequested && core.ShouldActivate(loadingSeg))
            {
                activationRequested = true;
                res.Activations.Add((loadingSeg, core.Frame));
                core.StopGating();
                continue;
            }

            core.SubmitLocal(new PadState { H = (sbyte)rng.Next(-127, 128), Buttons = (ushort)rng.Next(0, 512) });
            var sw = Stopwatch.StartNew();
            bool stalled = false;
            while (!core.TryGetInputs(pads))
            {
                stalled = true;
                client.Arrived.WaitOne(2);
                Pump();
                if (core.AbortReason != null) throw new Exception("aborted: " + core.AbortReason);
                if (sw.ElapsedMilliseconds > 10000) throw new Exception("stalled 10 s at seg " + core.Segment + " frame " + core.Frame + ", missing " + string.Join(",", core.MissingPlayers()));
            }
            if (stalled) res.Stalls++;

            var frameRng = new Random(unchecked(cfg.Seed ^ (core.Segment * 7919) ^ (core.Frame * 104729)));
            if (core.IsCheckFrame)
                core.RecordLocalState(Enumerable.Range(0, n).Select(p => new StateEntry { Key = "P" + p, X = pos[p], Health = hp[p] }).ToList());
            var fix = core.TakeCorrection();
            if (fix != null)
                foreach (var d in fix) { int p = int.Parse(d.Key.Substring(1)); pos[p] += d.X; hp[p] += d.Health; }

            for (int p = 0; p < n; p++)
            {
                pos[p] += pads[p].H / 127f * 0.05f + (float)frameRng.NextDouble() * 0.01f;
                if (pads[p].Has(PadState.Attack)) hp[(p + 1) % n] -= 0.001f;
            }
            if (injectDesync && core.Segment == 0 && core.Frame == 100) pos[0] += 1.5f;
            if (p2p != null && core.Segment == 0 && core.Frame == p2p.CutAtFrame && me <= 1) mux.CutLink(slots[1 - me], true);

            res.FrameHashes[((long)core.Segment << 32) | (uint)core.Frame] = string.Join("|", pos.Select(x => x.ToString("R"))) + "/" + string.Join("|", hp.Select(x => x.ToString("R")));

            // The round ends on the same simulated frame everywhere and the next stage starts loading.
            if (core.Frame == framesPerSegment && loadingSeg < 0)
            {
                if (core.Segment + 1 >= segments) break;
                StartLoad(core.Segment + 1);
            }
            core.Advance();
            if (jitterMs > 0) Thread.Sleep(rng.Next(0, jitterMs));
        }
        // Let the last state checks arrive before reporting.
        var end = Stopwatch.StartNew();
        while (end.ElapsedMilliseconds < 300) { Pump(); Thread.Sleep(10); }
        res.FinalPos = pos;
        res.Desyncs = core.DesyncCount;
        res.ChecksPassed = core.ChecksPassed;
    }

    // ------------------------------------------------------------------ other scenarios

    private static void LeaveScenario(int port)
    {
        var (clients, slots) = MakeRoom(port, 3);
        var cfg = new MatchConfig { Seed = 1, InputDelay = 2, Slots = slots, Names = new[] { "a", "b", "c" }, Picks = slots.Select(_ => new PlayerPick()).ToArray() };
        var cores = Enumerable.Range(0, 3).Select(i => new LockstepCore(cfg, i, i == 0, clients[i].Broadcast)).ToArray();
        clients[2].Dispose();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && (cores[0].AbortReason == null || cores[1].AbortReason == null))
        {
            for (int i = 0; i < 2; i++)
                while (clients[i].TryPoll(out var ev))
                    if (ev is EvPeerLeft pl) cores[i].OnPeerLeft(pl.Slot);
            Thread.Sleep(10);
        }
        Check(cores[0].AbortReason != null && cores[1].AbortReason != null, "remaining players were not told the match ended");
        clients[0].Dispose(); clients[1].Dispose();
    }

    private static void LobbyScenario(int port)
    {
        var host = Connect(port, "Host");
        host.CreateRoom(2);
        var room = WaitFor<EvRoomJoined>(host);
        Check(room.Code.Length == 5 && room.MySlot == 0 && room.HostSlot == 0 && room.MaxPlayers == 2, "bad room info");

        var guest = Connect(port, "Guest");
        guest.JoinRoom(room.Code.ToLowerInvariant());
        var gj = WaitFor<EvRoomJoined>(guest);
        Check(gj.MySlot == 1, "guest should get slot 1");
        Check(WaitFor<EvPeerJoined>(host).Name == "Guest", "host not told about guest");

        var third = Connect(port, "Third");
        third.JoinRoom(room.Code);
        Check(WaitFor<EvError>(third).Text.Contains("full"), "full room accepted a third player");
        third.JoinRoom("ZZZZZ");
        Check(WaitFor<EvError>(third).Text.Contains("No room"), "bad code not rejected");

        guest.Broadcast(GameMsg.Build(GameMsg.Pick, w => new PlayerPick { Character = "Gabumon", Color = 2, AltForm = 1 }.Write(w)));
        var data = WaitFor<EvData>(host);
        using (var r = GameMsg.Open(data.Payload, out byte t))
            Check(t == GameMsg.Pick && data.FromSlot == 1 && PlayerPick.Read(r).Character == "Gabumon", "pick not relayed intact");

        host.Dispose();
        Check(WaitFor<EvError>(guest).Text.Contains("host closed"), "guest not told the host left");

        // A client built for another protocol version is refused.
        var old = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        var s = old.GetStream();
        var hello = Wire.Build(Msg.Hello, w => { w.Write(Msg.ProtocolVersion + 1); w.Write("old"); });
        s.Write(hello, 0, hello.Length);
        Wire.ReadFrame(s); // welcome
        using (var r = Wire.Reader(Wire.ReadFrame(s), out byte type))
            Check(type == Msg.Error && r.ReadString().Contains("Version mismatch"), "protocol mismatch not reported");
        old.Close();
        guest.Dispose(); third.Dispose();
    }

    private static void BrowserScenario(int port)
    {
        var a = Connect(port, "Agumon");
        var b = Connect(port, "Gabumon");
        var c = Connect(port, "Patamon");
        var d = Connect(port, "Tentomon");
        a.CreateRoom(2, "Open room", RoomVisibility.Public, "");
        var open = WaitFor<EvRoomJoined>(a);
        Check(open.Name == "Open room" && open.Visibility == RoomVisibility.Public && !open.HasPassword, "open room info wrong");
        b.CreateRoom(4, "  Locked\u0007 room  ", RoomVisibility.Public, "secret");
        var locked = WaitFor<EvRoomJoined>(b);
        Check(locked.Name == "Locked room" && locked.HasPassword && locked.MaxPlayers == 4, "locked room info wrong: " + locked.Name);
        c.CreateRoom(2, "", RoomVisibility.Private, "");
        var priv = WaitFor<EvRoomJoined>(c);
        Check(priv.Name == "Patamon's room" && priv.Visibility == RoomVisibility.Private, "private room info wrong: " + priv.Name);

        d.ListRooms();
        var list = WaitFor<EvRoomList>(d);
        Check(list.PlayersOnline >= 4, "online count " + list.PlayersOnline);
        var openRow = list.Rooms.FirstOrDefault(r => r.Code == open.Code);
        var lockedRow = list.Rooms.FirstOrDefault(r => r.Code == locked.Code);
        Check(openRow != null && openRow.HostName == "Agumon" && openRow.Players == 1 && openRow.MaxPlayers == 2 && !openRow.Locked, "open room not listed right");
        Check(lockedRow != null && lockedRow.Locked && lockedRow.MaxPlayers == 4, "locked room not listed right");
        Check(list.Rooms.All(r => r.Code != priv.Code), "private room was listed");

        d.JoinRoom(locked.Code, "");
        Check(WaitFor<EvError>(d).Text.Contains("needs a password"), "locked room joined without password");
        d.JoinRoom(locked.Code, "wrong");
        Check(WaitFor<EvError>(d).Text.Contains("Wrong password"), "wrong password accepted");
        d.JoinRoom(locked.Code, "secret");
        Check(WaitFor<EvRoomJoined>(d).Code == locked.Code, "right password refused");
        d.LeaveRoom();
        d.JoinRoom(priv.Code.ToLowerInvariant());
        Check(WaitFor<EvRoomJoined>(d).Code == priv.Code, "private room not joinable by code");
        d.LeaveRoom();

        a.SetInMatch(true);
        Thread.Sleep(100);
        d.ListRooms();
        Check(WaitFor<EvRoomList>(d).Rooms.First(r => r.Code == open.Code).InMatch, "room not shown as in match");
        d.JoinRoom(open.Code);
        Check(WaitFor<EvError>(d).Text.Contains("in a match"), "joined a room during its match");
        a.SetInMatch(false);
        Thread.Sleep(100);
        d.JoinRoom(open.Code);
        Check(WaitFor<EvRoomJoined>(d).Code == open.Code, "room not joinable after its match");

        a.Dispose();
        Check(WaitFor<EvError>(d).Text.Contains("host closed"), "guest not told the host left");
        Thread.Sleep(100);
        d.ListRooms();
        Check(WaitFor<EvRoomList>(d).Rooms.All(r => r.Code != open.Code), "closed room still listed");
        b.Dispose(); c.Dispose(); d.Dispose();
    }

    private static void StatusScenario(int port)
    {
        using (var http = new System.Net.Http.HttpClient())
        {
            string json = http.GetStringAsync("http://127.0.0.1:" + port + "/status").Result;
            Check(json.Contains("\"protocol\":" + Msg.ProtocolVersion) && json.Contains("\"rooms\":"), "status page: " + json);
        }
    }

    private static void AddressScenario()
    {
        var a = ServerAddress.Parse("wss://lobby.example.com");
        Check(a.Tls && a.WebSocket && a.Port == 443 && a.Host == "lobby.example.com" && a.Path == "/" && a.HostHeader == "lobby.example.com", "wss parse");
        a = ServerAddress.Parse("ws://1.2.3.4:8080/netplay");
        Check(!a.Tls && a.WebSocket && a.Port == 8080 && a.Path == "/netplay" && a.HostHeader == "1.2.3.4:8080", "ws parse");
        a = ServerAddress.Parse("example.org:9000");
        Check(!a.WebSocket && a.Port == 9000 && a.Host == "example.org", "raw parse");
        a = ServerAddress.Parse("example.org");
        Check(!a.WebSocket && a.Port == ServerAddress.DefaultPort, "raw default port");
        a = ServerAddress.Parse("https://lobby.example.com/");
        Check(a.Tls && a.WebSocket, "https parse");
    }
}
