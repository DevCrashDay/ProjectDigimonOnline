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
        Run("spectator watches a 1v1 and loads slower than the players", () => Scenario(port, players: 2, delay: 3, segments: 3, framesPerSegment: 150, jitterMs: 4, injectDesync: false, spectators: 1));
        Run("two spectators watch a P2P 4-player match", () => Scenario(port, players: 4, delay: 3, segments: 2, framesPerSegment: 150, jitterMs: 4, injectDesync: false, p2p: new P2POptions(), spectators: 2));
        Run("spectator seats, in-match watching, spectator-only relay", () => SpectatorScenario(port));
        Run("quick match fills open 1v1 rooms first", () => QuickMatchScenario(port));
        Run("kick, room settings, host handover", () => KickAndSettingsScenario(port));
        Run("update notice in the welcome and version errors", () => UpdateNoticeScenario(port));
        string dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netplay-test-" + Guid.NewGuid().ToString("N"));
        var ranked = new RelayServer { DataDir = dataDir, ServerName = "Test <Server>" };
        ranked.Start(0, System.Net.IPAddress.Loopback);
        Run("ranked: matchmaking, Elo, forfeits, disputes, leaderboard", () => RankedScenario(ranked, dataDir));
        Run("problem reports are stored with limits", () => ReportScenario(ranked.Port, dataDir));
        ranked.Stop();
        try { System.IO.Directory.Delete(dataDir, true); } catch { }
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

    private static readonly Dictionary<RelayClient, string> RoomCodes = new Dictionary<RelayClient, string>();
    private static string RoomCodeOf(RelayClient c) { lock (RoomCodes) return RoomCodes[c]; }

    private static (RelayClient[] clients, int[] slots) MakeRoom(int port, int players)
    {
        var clients = new RelayClient[players];
        var slots = new int[players];
        clients[0] = Connect(port, "P0");
        clients[0].CreateRoom(players);
        var joined = WaitFor<EvRoomJoined>(clients[0]);
        slots[0] = joined.MySlot;
        lock (RoomCodes) RoomCodes[clients[0]] = joined.Code;
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
        public int Holds;      // spectator frames spent waiting for its own scene load
        public string Error;
    }

    private sealed class P2POptions
    {
        public double DropRate;
        public bool[] UdpOff = new bool[4];
        public int CutAtFrame = -1; // both ends of the 0<->1 link go dead at this frame of round 1
    }

    private static void Scenario(int port, int players, int delay, int segments, int framesPerSegment, int jitterMs, bool injectDesync, P2POptions p2p = null,
        int spectators = 0)
    {
        var (clients, slots) = MakeRoom(port, players);
        P2PMux[] muxes = p2p == null ? null : SetUpP2P(clients, slots, p2p);
        // Spectators join the room after the players; they load slowly so they must hold on scene switches.
        var specClients = new RelayClient[spectators];
        var specMuxes = new P2PMux[spectators];
        for (int i = 0; i < spectators; i++)
        {
            specClients[i] = Connect(port, "Watcher" + i);
            specClients[i].JoinRoom(RoomCodeOf(clients[0]), "", true);
            int specSlot = WaitFor<EvRoomJoined>(specClients[i]).MySlot;
            Check(specSlot >= Msg.FirstSpectatorSlot, "spectator got player slot " + specSlot);
            if (p2p != null) specMuxes[i] = new P2PMux(specSlot, false, specClients[i].Broadcast, specClients[i].Arrived);
        }
        if (muxes != null)
            for (int i = 0; i < players; i++) { var c = clients[i]; muxes[i].SpectatorBroadcast = spectators > 0 ? (Action<byte[]>)c.SendToSpectators : null; }
        Thread.Sleep(100);
        foreach (var c in clients) while (c.TryPoll(out _)) { }
        var cfg = new MatchConfig
        {
            Seed = 12345, InputDelay = delay, Stage = 1,
            Slots = slots, Names = slots.Select(s => "P" + s).ToArray(),
            Picks = slots.Select(_ => new PlayerPick { Character = "Agumon" }).ToArray(),
        };
        int total = players + spectators;
        var results = new PeerResult[total];
        var threads = new Thread[total];
        for (int i = 0; i < total; i++)
        {
            int me = i;
            results[me] = new PeerResult();
            threads[me] = new Thread(() =>
            {
                try
                {
                    if (me < players) SimPeer(clients[me], cfg, me, segments, framesPerSegment, jitterMs, injectDesync && me == 1, results[me], muxes?[me], p2p, slots);
                    else SimPeer(specClients[me - players], cfg, -1, segments, framesPerSegment, jitterMs, false, results[me], specMuxes[me - players], p2p, slots);
                }
                catch (Exception e) { results[me].Error = e.ToString(); }
            });
            threads[me].Start();
        }
        foreach (var t in threads) Check(t.Join(120000), "peer thread hung");
        foreach (var c in clients.Concat(specClients)) c.Dispose();
        if (muxes != null) foreach (var m in muxes.Concat(specMuxes)) m?.Dispose();

        for (int i = 0; i < total; i++) Check(results[i].Error == null, "peer " + i + " failed: " + results[i].Error);
        for (int i = players; i < total; i++) Check(results[i].Holds > 0 || segments == 1, "spectator " + i + " never had to hold for its slow load");
        for (int i = 1; i < total; i++)
            Check(results[i].Activations.SequenceEqual(results[0].Activations),
                "peers switched scenes on different frames: " + Fmt(results[0].Activations) + " vs " + Fmt(results[i].Activations));
        Check(results[0].Activations.Count == segments - 1, "expected " + (segments - 1) + " scene switches, got " + results[0].Activations.Count);

        if (!injectDesync)
        {
            for (int i = 1; i < total; i++)
            {
                Check(results[i].FrameHashes.Count == results[0].FrameHashes.Count, "different number of simulated frames");
                foreach (var kv in results[0].FrameHashes)
                    Check(results[i].FrameHashes.TryGetValue(kv.Key, out var h) && h == kv.Value,
                        "state diverged on peer " + i + " at segment " + (kv.Key >> 32) + " frame " + (int)kv.Key);
                Check(results[i].Desyncs == 0, "false desync reported on peer " + i);
                Check(results[i].ChecksPassed > 0, "no state checks ran on peer " + i);
            }
            Console.WriteLine("      " + results[0].FrameHashes.Count + " frames identical on " + players + " peers" + (spectators > 0 ? " and " + spectators + " spectator(s)" : "") + "; scene switches at " + Fmt(results[0].Activations) +
                              "; stalled frames per peer: " + string.Join(",", results.Select(r => r.Stalls)) +
                              (p2p != null ? "; match messages via server per peer: " + string.Join(",", results.Select(r => r.RelayedSeq)) : ""));
            if (p2p != null && p2p.CutAtFrame < 0 && !p2p.UdpOff.Any(x => x))
                Check(results.Take(players).All(r => r.RelayedSeq == 0), "P2P match fell back to the server although every link was up");
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
        bool spectator = me < 0;
        if (mux != null) mux.BeginMatch(cfg.Seed, cfg.Slots);
        var core = new LockstepCore(cfg, me, me == 0, mux != null ? (Action<byte[]>)mux.Send : client.Broadcast) { HostSlot = cfg.Slots[0] };
        var rng = new Random(spectator ? 999 : 1000 + me);
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
            int ms = spectator ? rng.Next(700, 1100) : rng.Next(30, 400);
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
            if (loadingSeg >= 0 && core.MustHold(loadingSeg))
            {
                res.Holds++;
                client.Arrived.WaitOne(5);
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
            if (p2p != null && core.Segment == 0 && core.Frame == p2p.CutAtFrame && me >= 0 && me <= 1) mux.CutLink(slots[1 - me], true);

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
        Check(WaitFor<EvHostChanged>(guest).HostSlot == 1, "guest did not become the host when the host left");

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
        Check(WaitFor<EvHostChanged>(d).HostSlot == 1, "host did not move to the remaining player");
        Thread.Sleep(100);
        d.ListRooms();
        Check(WaitFor<EvRoomList>(d).Rooms.First(r => r.Code == open.Code).HostName == "Tentomon", "room not kept with the new host");
        d.LeaveRoom();
        Thread.Sleep(100);
        d.ListRooms();
        Check(WaitFor<EvRoomList>(d).Rooms.All(r => r.Code != open.Code), "empty room still listed");
        b.Dispose(); c.Dispose(); d.Dispose();
    }

    private static RelayClient ConnectWithAccount(int port, string name, string token)
    {
        var c = new RelayClient { AccountToken = token };
        c.Connect("127.0.0.1", port, name);
        return c;
    }

    private static void Drain(params RelayClient[] cs) { Thread.Sleep(100); foreach (var c in cs) while (c.TryPoll(out _)) { } }

    private static void SpectatorScenario(int port)
    {
        var (players, slots) = MakeRoom(port, 2);
        string code = RoomCodeOf(players[0]);
        players[0].SetInMatch(true);
        Thread.Sleep(50);
        var late = Connect(port, "Late");
        late.JoinRoom(code);
        Check(WaitFor<EvError>(late).Text.Contains("watch"), "player joined mid-match");
        var watchers = new List<RelayClient>();
        for (int i = 0; i < Msg.MaxSpectators; i++)
        {
            var w = Connect(port, "W" + i);
            w.JoinRoom(code, "", true);
            Check(WaitFor<EvRoomJoined>(w).MySlot == Msg.FirstSpectatorSlot + i, "spectator seat " + i);
            watchers.Add(w);
        }
        late.JoinRoom(code, "", true);
        Check(WaitFor<EvError>(late).Text.Contains("spectator seats"), "ninth spectator accepted");
        late.ListRooms();
        var row = WaitFor<EvRoomList>(late).Rooms.First(r => r.Code == code);
        Check(row.Players == 2 && row.Spectators == Msg.MaxSpectators && row.InMatch, "room row counts spectators as players: " + row.Players + "/" + row.Spectators);
        Drain(players.Concat(watchers).ToArray());

        players[1].Broadcast(new byte[] { 42 });
        Check(WaitFor<EvData>(watchers[3]).FromSlot == slots[1], "spectator did not get the players' messages");
        Check(WaitFor<EvData>(players[0]).Payload[0] == 42, "player did not get the message");
        Drain(players.Concat(watchers).ToArray());
        players[0].SendToSpectators(new byte[] { 7 });
        Check(WaitFor<EvData>(watchers[0]).Payload[0] == 7, "spectator-only relay missed a spectator");
        Thread.Sleep(150);
        bool leaked = false;
        while (players[1].TryPoll(out var ev)) if (ev is EvData d && d.Payload[0] == 7) leaked = true;
        Check(!leaked, "spectator-only relay reached a player");

        // The last player leaving closes the room for the spectators.
        players[0].Dispose();
        players[1].Dispose();
        Check(WaitFor<EvRoomLeft>(watchers[5]).Reason.Contains("closed"), "spectators not told the room closed");
        foreach (var w in watchers) w.Dispose();
        late.Dispose();
    }

    private static void QuickMatchScenario(int port)
    {
        var a = Connect(port, "QA");
        a.QuickMatch();
        var ra = WaitFor<EvRoomJoined>(a);
        Check(ra.MaxPlayers == 2 && ra.Visibility == RoomVisibility.Public && !ra.HasPassword, "quick match did not open a public 1v1");
        var locked = Connect(port, "QL");
        locked.CreateRoom(2, "pw", RoomVisibility.Public, "x");
        WaitFor<EvRoomJoined>(locked);
        var b = Connect(port, "QB");
        b.QuickMatch();
        Check(WaitFor<EvRoomJoined>(b).Code == ra.Code, "quick match did not join the waiting player");
        var c = Connect(port, "QC");
        c.QuickMatch();
        var rc = WaitFor<EvRoomJoined>(c);
        Check(rc.Code != ra.Code && rc.MySlot == 0, "quick match joined a full or locked room");
        a.Dispose(); b.Dispose(); c.Dispose(); locked.Dispose();
    }

    private static void KickAndSettingsScenario(int port)
    {
        var host = Connect(port, "KHost");
        host.CreateRoom(4, "Big room");
        var room = WaitFor<EvRoomJoined>(host);
        var g1 = Connect(port, "KG1");
        var g2 = Connect(port, "KG2");
        g1.JoinRoom(room.Code); WaitFor<EvRoomJoined>(g1);
        g2.JoinRoom(room.Code); Check(WaitFor<EvRoomJoined>(g2).MySlot == 2, "third player not in slot 2");
        Drain(host, g1, g2);

        g1.Kick(0); // not the host: ignored
        host.UpdateRoom(2, "Small room", RoomVisibility.Public, false, "");
        Check(WaitFor<EvError>(host).Text.Contains("too many players"), "room shrank below its player count");

        host.Kick(1);
        Check(WaitFor<EvRoomLeft>(g1).Reason.Contains("removed"), "kicked player not told");
        Check(WaitFor<EvPeerLeft>(host).Slot == 1, "host not told about the kick");
        g1.JoinRoom(room.Code);
        Check(WaitFor<EvError>(g1).Text.Contains("removed you"), "kicked player could rejoin");

        // Shrinking to 1v1 moves the player in slot 2 down to slot 1.
        host.UpdateRoom(2, "Small room", RoomVisibility.Public, true, "pw");
        var moved = WaitFor<EvRoomJoined>(g2);
        Check(moved.MySlot == 1 && moved.MaxPlayers == 2, "player not moved into the smaller room");
        var upd = WaitFor<EvRoomUpdated>(host);
        Check(upd.MaxPlayers == 2 && upd.Name == "Small room" && upd.HasPassword, "room update not announced");
        g1.ListRooms();
        var row = WaitFor<EvRoomList>(g1).Rooms.First(r => r.Code == room.Code);
        Check(row.Locked && row.MaxPlayers == 2 && row.Players == 2 && row.Name == "Small room", "listing does not show the new settings");

        // The host leaving hands the room over instead of closing it.
        Drain(g2);
        host.Dispose();
        Check(WaitFor<EvHostChanged>(g2).HostSlot == 1, "room not handed over");
        g2.UpdateRoom(2, "Mine now", RoomVisibility.Private, true, "");
        Check(WaitFor<EvRoomUpdated>(g2).Name == "Mine now", "new host cannot change settings");
        g1.Dispose(); g2.Dispose();
    }

    private static void UpdateNoticeScenario(int port)
    {
        var c = new RelayClient();
        c.Connect("127.0.0.1", port, "Up");
        var w = WaitFor<EvWelcome>(c);
        Check(w.LatestVersion == BuildInfo.Version && w.DownloadUrl.StartsWith("https://github.com/"), "welcome lacks version info: " + w.LatestVersion + " " + w.DownloadUrl);
        c.Dispose();
        var old = new RelayClient { Protocol = Msg.ProtocolVersion - 1 };
        old.Connect("127.0.0.1", port, "Old");
        var e = WaitFor<EvError>(old);
        Check(e.Text.Contains("Version mismatch") && e.Text.Contains(BuildInfo.ReleasesUrl), "mismatch error lacks the download link: " + e.Text);
        old.Dispose();
        Check(BuildInfo.IsNewer("0.10.0", "0.9.9") && !BuildInfo.IsNewer("0.5.0", "0.5.0") && BuildInfo.IsNewer("1.0", "0.9.9") && !BuildInfo.IsNewer("0.4.1", "v0.5.0"), "version compare");
    }

    private static void RankedScenario(RelayServer server, string dataDir)
    {
        int port = server.Port;
        var a = ConnectWithAccount(port, "Alice", "token-alice-0123456789");
        var b = ConnectWithAccount(port, "Bob", "token-bob-0123456789ab");
        var acc = WaitFor<EvAccount>(a);
        Check(acc.Display.StartsWith("Alice#") && acc.Rating == 1000 && acc.Rank == 0, "new account info wrong: " + acc.Display + " " + acc.Rating);
        var noAccount = Connect(port, "Anon");
        noAccount.QueueRanked();
        Check(WaitFor<EvError>(noAccount).Text.Contains("Ranked"), "player without an account was queued");

        RelayClient[] Match()
        {
            a.QueueRanked();
            b.QueueRanked();
            var ja = WaitFor<EvRoomJoined>(a, 5000);
            var jb = WaitFor<EvRoomJoined>(b, 5000);
            Check(ja.Ranked && jb.Ranked && ja.Code == jb.Code && ja.MaxPlayers == 2, "ranked room not made");
            Drain(a, b);
            return ja.MySlot == 0 ? new[] { a, b } : new[] { b, a };
        }

        // 1. Both report the same winner.
        var m = Match();
        var watcher = Connect(port, "Joiner");
        watcher.ListRooms();
        var row = WaitFor<EvRoomList>(watcher).Rooms.First(r => r.Ranked);
        watcher.JoinRoom(row.Code);
        Check(WaitFor<EvError>(watcher).Text.Contains("Ranked rooms"), "player joined a ranked room");
        watcher.JoinRoom(row.Code, "", true);
        Check(WaitFor<EvRoomJoined>(watcher).MySlot >= Msg.FirstSpectatorSlot, "could not watch a ranked room");
        m[0].Kick(1);
        Check(WaitFor<EvError>(m[0]).Text.Contains("ranked"), "kick allowed in a ranked room");
        m[0].SetInMatch(true, 111);
        Thread.Sleep(50);
        m[0].ReportResult(111, 0);
        m[1].ReportResult(111, 0);
        var r0 = WaitFor<EvRatingChanged>(m[0]);
        var r1 = WaitFor<EvRatingChanged>(m[1]);
        Check(r0.Result == 1 && r0.NewRating == 1020 && r1.Result == 0 && r1.NewRating == 980, "Elo after the first game: " + r0.NewRating + " / " + r1.NewRating);
        m[0].SetInMatch(false);

        // 2. A player leaving mid-match forfeits.
        m[0].LeaveRoom(); m[1].LeaveRoom();
        Thread.Sleep(50);
        m = Match();
        m[0].SetInMatch(true, 222);
        Thread.Sleep(50);
        int before = server.Ratings.Top(10).First(x => x.Name.StartsWith(m[1] == a ? "Alice" : "Bob")).Rating;
        m[0].LeaveRoom();
        var forfeit = WaitFor<EvRatingChanged>(m[1]);
        Check(forfeit.Result == 1 && forfeit.NewRating > before, "leaving mid-match did not count as a loss");

        // 3. Different results change nothing.
        m[1].LeaveRoom();
        Thread.Sleep(50);
        m = Match();
        m[0].SetInMatch(true, 333);
        Thread.Sleep(50);
        var snapshot = string.Join(",", server.Ratings.Top(10).Select(x => x.Rating));
        m[0].ReportResult(333, 0);
        m[1].ReportResult(333, 1);
        Check(WaitFor<EvError>(m[0]).Text.Contains("different results"), "dispute not reported");
        Check(string.Join(",", server.Ratings.Top(10).Select(x => x.Rating)) == snapshot, "disputed game changed ratings");
        m[0].LeaveRoom(); m[1].LeaveRoom();

        // 4. Far-apart ratings only meet once the search has widened.
        var c = ConnectWithAccount(port, "Carol", "token-carol-0123456789");
        WaitFor<EvAccount>(c);
        server.Ratings.Get(RatingStore.KeyOf("token-carol-0123456789"), null).Rating = 1700;
        a.QueueRanked();
        c.QueueRanked();
        Thread.Sleep(1500);
        bool early = false;
        while (a.TryPoll(out var ev)) if (ev is EvRoomJoined) early = true;
        Check(!early, "players 700 points apart were matched at once");
        Check(WaitFor<EvQueue>(c).Searching, "queue status not sent");
        server.QueueWidenPerSecond = 1000;
        Check(WaitFor<EvRoomJoined>(c, 5000).Ranked, "wide search never matched");
        server.QueueWidenPerSecond = 25;
        a.LeaveRoom(); c.LeaveRoom();

        // Leaderboard in the game and on the web.
        a.GetLeaderboard();
        var lb = WaitFor<EvLeaderboard>(a);
        Check(lb.Rows.Length == 2 && lb.Rows[0].Rank == 1 && lb.Rows[0].Rating >= lb.Rows[1].Rating && lb.Rows.All(x => x.Name.Contains("#")), "leaderboard rows wrong");
        Check(WaitFor<EvAccount>(a).Rank >= 1, "rank not sent with the leaderboard");
        using (var http = new System.Net.Http.HttpClient())
        {
            string json = http.GetStringAsync("http://127.0.0.1:" + port + "/leaderboard.json").Result;
            Check(json.Contains("\"rank\":1") && json.Contains("Alice#") && json.Contains("Bob#") && !json.Contains("Carol"), "leaderboard.json: " + json);
            string html = http.GetStringAsync("http://127.0.0.1:" + port + "/leaderboard").Result;
            Check(html.Contains("<table") && html.Contains("Alice#") && html.Contains("Test &lt;Server&gt;"), "leaderboard page not rendered or not escaped");
        }
        // Ratings survive a restart.
        var reloaded = new RatingStore(System.IO.Path.Combine(dataDir, "ratings.tsv"));
        Check(reloaded.Top(10).Select(x => x.Rating).SequenceEqual(server.Ratings.Top(10).Select(x => x.Rating)), "ratings file does not match");
        Console.WriteLine("      final ratings: " + string.Join(", ", server.Ratings.Top(10).Select(x => x.Name.Split('#')[0] + " " + x.Rating + " (" + x.Wins + "-" + x.Losses + ")")));
        a.Dispose(); b.Dispose(); c.Dispose(); watcher.Dispose(); noAccount.Dispose();
    }

    private static byte[] Gzip(string text)
    {
        using (var ms = new System.IO.MemoryStream())
        {
            using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress))
            {
                var b = System.Text.Encoding.UTF8.GetBytes(text);
                z.Write(b, 0, b.Length);
            }
            return ms.ToArray();
        }
    }

    private static void ReportScenario(int port, string dataDir)
    {
        var c = Connect(port, "Reporter");
        var bigLog = string.Concat(Enumerable.Repeat("[Info   :Digimon Netplay] some log line\n", 5000));
        c.SendReport("Game froze after round 2", Gzip(bigLog));
        var saved = WaitFor<EvReportSaved>(c);
        var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(dataDir, "reports"));
        Check(files.Length == 1 && files[0].Contains(saved.Id), "report file missing");
        string text = System.IO.File.ReadAllText(files[0]);
        Check(text.Contains("Game froze after round 2") && text.Contains("Mod version: " + BuildInfo.Version) && text.Length > bigLog.Length, "report content wrong");
        c.SendReport("again", Gzip("x"));
        Check(WaitFor<EvError>(c).Text.Contains("wait a minute"), "report rate limit missing");
        c.Dispose();
        // A server without a data folder says it does not take reports.
        var plain = new RelayServer();
        plain.Start(0, System.Net.IPAddress.Loopback);
        var d = Connect(plain.Port, "R2");
        d.SendReport("x", Gzip("x"));
        Check(WaitFor<EvError>(d).Text.Contains("does not collect"), "reports accepted without a data folder");
        d.Dispose();
        plain.Stop();
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
