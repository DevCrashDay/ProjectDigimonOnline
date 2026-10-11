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
        c.Connect("127.0.0.1", port, name);
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
        public string Error;
    }

    private static void Scenario(int port, int players, int delay, int segments, int framesPerSegment, int jitterMs, bool injectDesync)
    {
        var (clients, slots) = MakeRoom(port, players);
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
                try { SimPeer(clients[me], cfg, me, segments, framesPerSegment, jitterMs, injectDesync && me == 1, results[me]); }
                catch (Exception e) { results[me].Error = e.ToString(); }
            });
            threads[me].Start();
        }
        foreach (var t in threads) Check(t.Join(120000), "peer thread hung");
        foreach (var c in clients) c.Dispose();

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
                              "; stalled frames per peer: " + string.Join(",", results.Select(r => r.Stalls)));
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
    private static void SimPeer(RelayClient client, MatchConfig cfg, int me, int segments, int framesPerSegment, int jitterMs, bool injectDesync, PeerResult res)
    {
        var core = new LockstepCore(cfg, me, me == 0, client.Broadcast);
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
                if (ev is EvData d) core.Handle(d.FromSlot, d.Payload);
                else if (ev is EvPeerLeft pl) core.OnPeerLeft(pl.Slot);
                else if (ev is EvDisconnected) core.Abort("disconnected", false);
            }
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
}
