using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DigimonNetplay.Shared
{
    /// <summary>One player's controller state for one simulation frame.</summary>
    public struct PadState : IEquatable<PadState>
    {
        public const ushort Attack = 1, Special = 2, Jump = 4, Protect = 8, Evolve = 16, Grab = 32, Crouch = 64, R = 128, L = 256;

        public sbyte H, V, CrouchAxis;
        public ushort Buttons;

        public bool Has(ushort bit) => (Buttons & bit) != 0;

        public static float AxisToFloat(sbyte v) => v / 127f;
        public static sbyte FloatToAxis(float f) => (sbyte)Math.Round(Math.Max(-1f, Math.Min(1f, f)) * 127f);

        public static ushort BitFor(string action)
        {
            switch (action)
            {
                case "Attack": return Attack;
                case "Special": return Special;
                case "Jump": return Jump;
                case "Protect": return Protect;
                case "Evolve": return Evolve;
                case "Grab": return Grab;
                case "Crouch": return Crouch;
                case "R": return R;
                case "L": return L;
                default: return 0; // Start/pause and anything unknown are never sent over the network
            }
        }

        public void Write(BinaryWriter w) { w.Write(H); w.Write(V); w.Write(CrouchAxis); w.Write(Buttons); }
        public static PadState Read(BinaryReader r) => new PadState { H = r.ReadSByte(), V = r.ReadSByte(), CrouchAxis = r.ReadSByte(), Buttons = r.ReadUInt16() };

        public bool Equals(PadState o) => H == o.H && V == o.V && CrouchAxis == o.CrouchAxis && Buttons == o.Buttons;
        public override bool Equals(object obj) => obj is PadState p && Equals(p);
        public override int GetHashCode() => (H << 24) ^ (V << 16) ^ (CrouchAxis << 8) ^ Buttons;
    }

    public sealed class PlayerPick
    {
        public string Character = "";
        public int Color = 1;
        public int AltForm = 1;

        public void Write(BinaryWriter w) { w.Write(Character ?? ""); w.Write(Color); w.Write(AltForm); }
        public static PlayerPick Read(BinaryReader r) => new PlayerPick { Character = r.ReadString(), Color = r.ReadInt32(), AltForm = r.ReadInt32() };
        public PlayerPick Clone() => new PlayerPick { Character = Character, Color = Color, AltForm = AltForm };
    }

    /// <summary>Everything every peer needs to start the same battle.</summary>
    public sealed class MatchConfig
    {
        public int Seed;
        public int InputDelay = 3;
        public int Stage;
        public int Song;
        public bool BattleRoyale;
        public int[] Slots = new int[0];          // network slot of game player 0..n-1
        public string[] Names = new string[0];
        public PlayerPick[] Picks = new PlayerPick[0];
        // Host's battle settings, applied on every peer for the duration of the match.
        public Dictionary<string, string> Settings = new Dictionary<string, string>();

        public int PlayerCount => Slots.Length;
        public int PlayerOfSlot(int slot) => Array.IndexOf(Slots, slot);

        public void Write(BinaryWriter w)
        {
            w.Write(Seed); w.Write((byte)InputDelay); w.Write(Stage); w.Write(Song); w.Write(BattleRoyale);
            w.Write((byte)Slots.Length);
            for (int i = 0; i < Slots.Length; i++) { w.Write((byte)Slots[i]); w.Write(Names[i] ?? ""); Picks[i].Write(w); }
            w.Write(Settings.Count);
            foreach (var kv in Settings) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
        }

        public static MatchConfig Read(BinaryReader r)
        {
            var c = new MatchConfig { Seed = r.ReadInt32(), InputDelay = r.ReadByte(), Stage = r.ReadInt32(), Song = r.ReadInt32(), BattleRoyale = r.ReadBoolean() };
            int n = r.ReadByte();
            c.Slots = new int[n]; c.Names = new string[n]; c.Picks = new PlayerPick[n];
            for (int i = 0; i < n; i++) { c.Slots[i] = r.ReadByte(); c.Names[i] = r.ReadString(); c.Picks[i] = PlayerPick.Read(r); }
            int s = r.ReadInt32();
            for (int i = 0; i < s; i++) c.Settings[r.ReadString()] = r.ReadString();
            return c;
        }
    }

    /// <summary>One tracked object in a desync check (a fighter, the timer, ...).</summary>
    public struct StateEntry
    {
        public string Key;
        public float X, Y, Z, Health, Energy;

        public void Write(BinaryWriter w) { w.Write(Key ?? ""); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Health); w.Write(Energy); }
        public static StateEntry Read(BinaryReader r) => new StateEntry { Key = r.ReadString(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Health = r.ReadSingle(), Energy = r.ReadSingle() };
    }

    /// <summary>Payloads carried inside relay frames between game clients.</summary>
    public static class GameMsg
    {
        public const byte Pick = 1;        // PlayerPick
        public const byte Lobby = 2;       // LobbyInfo (host -> all)
        public const byte Start = 3;       // MatchConfig (host -> all)
        public const byte Ready = 4;       // int segment, int frame
        public const byte Input = 5;       // int segment, int frame, PadState
        public const byte StateCheck = 6;  // int segment, int frame, entries (host -> all)
        public const byte Abort = 7;       // string reason

        public static byte[] Build(byte type, Action<BinaryWriter> body = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(type);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Open(byte[] payload, out byte type)
        {
            type = payload[0];
            return new BinaryReader(new MemoryStream(payload, 1, payload.Length - 1), Encoding.UTF8);
        }
    }

    /// <summary>Lobby settings the host shares while players are picking.</summary>
    public sealed class LobbyInfo
    {
        public int Stage;
        public int InputDelay = 3;
        public int Rounds = 1;
        public Dictionary<int, PlayerPick> Picks = new Dictionary<int, PlayerPick>();

        public void Write(BinaryWriter w)
        {
            w.Write(Stage); w.Write((byte)InputDelay); w.Write((byte)Rounds);
            w.Write((byte)Picks.Count);
            foreach (var kv in Picks) { w.Write((byte)kv.Key); kv.Value.Write(w); }
        }

        public static LobbyInfo Read(BinaryReader r)
        {
            var l = new LobbyInfo { Stage = r.ReadInt32(), InputDelay = r.ReadByte(), Rounds = r.ReadByte() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++) { int slot = r.ReadByte(); l.Picks[slot] = PlayerPick.Read(r); }
            return l;
        }
    }
}
