using System;
using System.Collections.Generic;
using System.IO;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Engine-independent delay-based lockstep. Every peer simulates the whole battle; each frame it may only
    /// advance once it holds every player's input for that frame. A player's input sampled at frame F is
    /// scheduled for frame F + InputDelay, so on a good connection nobody ever waits.
    ///
    /// A "segment" is one loaded stage scene (each round reloads the stage). Frames count from 0 per segment.
    /// Scene switches are agreed on: each peer reports Ready when its next scene has loaded in the background,
    /// and everyone activates it on the same simulation frame.
    /// </summary>
    public sealed class LockstepCore
    {
        public const int CheckInterval = 30;
        public const float PosTolerance = 0.02f;
        public const float BarTolerance = 0.002f;

        private readonly Action<byte[]> _broadcast;
        private readonly Dictionary<long, PadState>[] _inputs;
        private readonly Dictionary<int, Dictionary<int, int>> _ready = new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<long, List<StateEntry>> _localChecks = new Dictionary<long, List<StateEntry>>();
        private readonly Dictionary<long, List<StateEntry>> _hostChecks = new Dictionary<long, List<StateEntry>>();
        private readonly List<StateEntry> _pendingCorrection = new List<StateEntry>();

        public MatchConfig Config { get; }
        public int LocalPlayer { get; }
        public bool IsHost { get; }
        public int Delay => Config.InputDelay;
        public int Players => Config.PlayerCount;

        public int Segment { get; private set; } = -1;
        public int Frame { get; private set; }
        public bool Gating { get; private set; }
        public int DesyncCount { get; private set; }
        public int ChecksPassed { get; private set; }
        public string AbortReason { get; private set; }
        public Action<string> Log = _ => { };

        public LockstepCore(MatchConfig config, int localPlayer, bool isHost, Action<byte[]> broadcast)
        {
            Config = config;
            LocalPlayer = localPlayer;
            IsHost = isHost;
            _broadcast = broadcast;
            _inputs = new Dictionary<long, PadState>[config.PlayerCount];
            for (int i = 0; i < _inputs.Length; i++) _inputs[i] = new Dictionary<long, PadState>();
        }

        private static long Key(int segment, int frame) => ((long)segment << 32) | (uint)frame;

        // ---------------------------------------------------------------- scene switching

        /// <summary>The local peer has the scene for <paramref name="segment"/> loaded in the background.</summary>
        public void LocalLoadReady(int segment)
        {
            int frame = Gating ? Frame : -1;
            NoteReady(segment, LocalPlayer, frame);
            _broadcast(GameMsg.Build(GameMsg.Ready, w => { w.Write(segment); w.Write(frame); }));
        }

        private void NoteReady(int segment, int player, int frame)
        {
            if (!_ready.TryGetValue(segment, out var map)) _ready[segment] = map = new Dictionary<int, int>();
            map[player] = frame;
        }

        /// <summary>
        /// The simulation frame on which <paramref name="segment"/> must be activated, -1 to activate right away,
        /// or null while some peer is still loading.
        /// </summary>
        public int? ActivationFrame(int segment)
        {
            if (!_ready.TryGetValue(segment, out var map) || map.Count < Players) return null;
            int max = -1;
            foreach (int f in map.Values) max = Math.Max(max, f);
            // A peer that sent Ready at frame f cannot be more than Delay frames ahead of anyone else, so every
            // peer has heard every Ready before reaching max + Delay + 1.
            return max < 0 ? -1 : max + Delay + 1;
        }

        public bool ShouldActivate(int segment)
        {
            int? a = ActivationFrame(segment);
            if (a == null) return false;
            if (!Gating || a.Value < 0) return true;
            if (Frame > a.Value) Log("activation frame " + a.Value + " already passed (at " + Frame + ")");
            return Frame >= a.Value;
        }

        /// <summary>The new stage scene is live; start counting frames for it.</summary>
        public void BeginSegment(int segment)
        {
            Segment = segment;
            Frame = 0;
            Gating = true;
            _ready.Remove(segment);
            Prune();
        }

        /// <summary>Stop gating (scene is switching or the match ended).</summary>
        public void StopGating() => Gating = false;

        // ---------------------------------------------------------------- inputs

        /// <summary>Local input sampled this frame; it will be used at Frame + Delay.</summary>
        public void SubmitLocal(PadState pad)
        {
            int seg = Segment, target = Frame + Delay;
            _inputs[LocalPlayer][Key(seg, target)] = pad;
            _broadcast(GameMsg.Build(GameMsg.Input, w => { w.Write(seg); w.Write(target); pad.Write(w); }));
        }

        /// <summary>Fills <paramref name="pads"/> for the current frame, or returns false if someone's input is missing.</summary>
        public bool TryGetInputs(PadState[] pads)
        {
            if (Frame < Delay)
            {
                for (int p = 0; p < Players; p++) pads[p] = default(PadState);
                return true;
            }
            long k = Key(Segment, Frame);
            for (int p = 0; p < Players; p++)
                if (!_inputs[p].TryGetValue(k, out pads[p])) return false;
            return true;
        }

        /// <summary>Which players we are still waiting on for the current frame (for the HUD).</summary>
        public List<int> MissingPlayers()
        {
            var list = new List<int>();
            if (Frame < Delay) return list;
            long k = Key(Segment, Frame);
            for (int p = 0; p < Players; p++) if (!_inputs[p].ContainsKey(k)) list.Add(p);
            return list;
        }

        public void Advance()
        {
            Frame++;
            if ((Frame & 127) == 0) Prune();
        }

        private void Prune()
        {
            long keep = Key(Segment, Math.Max(0, Frame - 600));
            foreach (var d in _inputs) RemoveBelow(d, keep);
            RemoveBelow(_localChecks, keep);
            RemoveBelow(_hostChecks, keep);
        }

        private static void RemoveBelow<T>(Dictionary<long, T> d, long keep)
        {
            List<long> dead = null;
            foreach (long k in d.Keys) if (k < keep) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) d.Remove(k);
        }

        // ---------------------------------------------------------------- desync checks

        public bool IsCheckFrame => Gating && Frame > 0 && Frame % CheckInterval == 0;

        /// <summary>Record this peer's state at the start of the current frame. The host also shares it.</summary>
        public void RecordLocalState(List<StateEntry> entries)
        {
            int seg = Segment, frame = Frame;
            long k = Key(seg, frame);
            _localChecks[k] = entries;
            if (IsHost)
            {
                _broadcast(GameMsg.Build(GameMsg.StateCheck, w =>
                {
                    w.Write(seg); w.Write(frame); w.Write(entries.Count);
                    foreach (var e in entries) e.Write(w);
                }));
            }
            else Compare(k);
        }

        private void Compare(long k)
        {
            if (!_localChecks.TryGetValue(k, out var mine) || !_hostChecks.TryGetValue(k, out var host)) return;
            _localChecks.Remove(k);
            _hostChecks.Remove(k);
            var deltas = new List<StateEntry>();
            foreach (var h in host)
            {
                int i = mine.FindIndex(m => m.Key == h.Key);
                if (i < 0) continue;
                var m0 = mine[i];
                var d = new StateEntry { Key = h.Key, X = h.X - m0.X, Y = h.Y - m0.Y, Z = h.Z - m0.Z, Health = h.Health - m0.Health, Energy = h.Energy - m0.Energy };
                if (Math.Abs(d.X) > PosTolerance || Math.Abs(d.Y) > PosTolerance || Math.Abs(d.Z) > PosTolerance ||
                    Math.Abs(d.Health) > BarTolerance || Math.Abs(d.Energy) > BarTolerance)
                    deltas.Add(d);
            }
            if (deltas.Count == 0) { ChecksPassed++; return; }
            DesyncCount++;
            Log("desync at segment " + (k >> 32) + " frame " + (int)k + ": " + deltas.Count + " object(s) differ, correcting toward host");
            lock (_pendingCorrection) _pendingCorrection.AddRange(deltas);
        }

        /// <summary>Offsets (host minus local) to add to local objects so this peer drifts back to the host.</summary>
        public List<StateEntry> TakeCorrection()
        {
            lock (_pendingCorrection)
            {
                if (_pendingCorrection.Count == 0) return null;
                var list = new List<StateEntry>(_pendingCorrection);
                _pendingCorrection.Clear();
                return list;
            }
        }

        // ---------------------------------------------------------------- network

        public void Abort(string reason, bool tellOthers)
        {
            if (AbortReason != null) return;
            AbortReason = reason;
            if (tellOthers) _broadcast(GameMsg.Build(GameMsg.Abort, w => w.Write(reason)));
        }

        public void OnPeerLeft(int slot)
        {
            if (Config.PlayerOfSlot(slot) >= 0) Abort("A player left the match.", false);
        }

        /// <summary>Handle a game payload relayed from another peer. Returns false if it was not a lockstep message.</summary>
        public bool Handle(int fromSlot, byte[] payload)
        {
            int player = Config.PlayerOfSlot(fromSlot);
            using (BinaryReader r = GameMsg.Open(payload, out byte type))
            {
                switch (type)
                {
                    case GameMsg.Input:
                    {
                        if (player < 0) return true;
                        int seg = r.ReadInt32(), frame = r.ReadInt32();
                        _inputs[player][Key(seg, frame)] = PadState.Read(r);
                        return true;
                    }
                    case GameMsg.Ready:
                    {
                        if (player < 0) return true;
                        int seg = r.ReadInt32(), frame = r.ReadInt32();
                        NoteReady(seg, player, frame);
                        return true;
                    }
                    case GameMsg.StateCheck:
                    {
                        int seg = r.ReadInt32(), frame = r.ReadInt32(), n = r.ReadInt32();
                        var list = new List<StateEntry>(n);
                        for (int i = 0; i < n; i++) list.Add(StateEntry.Read(r));
                        long k = Key(seg, frame);
                        _hostChecks[k] = list;
                        Compare(k);
                        return true;
                    }
                    case GameMsg.Abort:
                        Abort(r.ReadString(), false);
                        return true;
                    default:
                        return false;
                }
            }
        }
    }
}
