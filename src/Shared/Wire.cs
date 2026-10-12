using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DigimonNetplay.Shared
{
    /// <summary>Message ids exchanged between a client and the relay server.</summary>
    public static class Msg
    {
        public const int ProtocolVersion = 4;

        // server -> client
        public const byte Welcome = 1;      // int clientId, string serverName, string latestVersion, string downloadUrl
        public const byte RoomJoined = 2;   // string code, byte mySlot, byte hostSlot, byte maxPlayers, string roomName, byte visibility, bool hasPassword, bool ranked
        public const byte PeerJoined = 3;   // byte slot, string name, int rating (0 = unranked); slots >= FirstSpectatorSlot are spectators
        public const byte PeerLeft = 4;     // byte slot
        public const byte Error = 5;        // string text
        public const byte Relayed = 6;      // byte fromSlot, bytes payload
        public const byte Pong = 8;         // long clientTicks
        public const byte RoomList = 9;     // int playersOnline, int count, count x RoomInfo
        public const byte HostChanged = 10; // byte newHostSlot
        public const byte RoomUpdated = 11; // byte maxPlayers, string roomName, byte visibility, bool hasPassword
        public const byte RoomLeft = 12;    // string reason (you are no longer in a room: kicked, room closed)
        public const byte ReportSaved = 13; // string reportId
        public const byte AccountInfo = 14; // string displayName ("Name#TAG"), int rating, int wins, int losses, int rank (0 = unranked)
        public const byte QueueStatus = 15; // bool searching, int secondsWaited, int playersSearching
        public const byte RatingChanged = 16; // int oldRating, int newRating, byte result (0 loss, 1 win, 2 draw), string opponent
        public const byte Leaderboard = 17; // int count, count x LeaderRow

        // client -> server
        public const byte Hello = 101;      // int protocol, string name, string modVersion, string accountToken ("" = none)
        public const byte Create = 102;     // byte maxPlayers, string roomName, byte visibility, string password ("" = none)
        public const byte Join = 103;       // string code, string password, bool spectate
        public const byte Leave = 104;
        public const byte Relay = 105;      // bytes payload, forwarded to every other member of the room (players and spectators)
        public const byte Ping = 106;       // long clientTicks
        public const byte ListRooms = 107;
        public const byte SetInMatch = 108; // bool inMatch, int matchId (host only; listed as "in match", players cannot join)
        public const byte QuickMatch = 109; // join the best open public room, or open a new 1v1 room
        public const byte Kick = 110;       // byte slot (host only)
        public const byte UpdateRoom = 111; // byte maxPlayers, string roomName, byte visibility, bool changePassword, string password (host only)
        public const byte Report = 112;     // string description, string modVersion, bytes gzippedLog
        public const byte QueueRanked = 113;
        public const byte CancelQueue = 114;
        public const byte MatchResult = 115; // int matchId, byte winnerSlot (255 = draw / no winner)
        public const byte GetLeaderboard = 116;
        public const byte RelaySpectators = 117; // bytes payload, forwarded to the room's spectators only

        public const int FirstSpectatorSlot = 16;
        public const int MaxSpectators = 8;
        public const byte NoWinner = 255;
    }

    public static class RoomVisibility
    {
        public const byte Public = 0;   // listed in the lobby browser (optionally with a password)
        public const byte Private = 1;  // not listed; joined by room code only
    }

    /// <summary>One row of the lobby browser.</summary>
    public sealed class RoomInfo
    {
        public string Code, Name, HostName;
        public int Players, MaxPlayers, Spectators;
        public bool Locked, InMatch, Ranked;

        public void Write(BinaryWriter w)
        {
            w.Write(Code); w.Write(Name); w.Write(HostName);
            w.Write((byte)Players); w.Write((byte)MaxPlayers); w.Write((byte)Spectators);
            w.Write(Locked); w.Write(InMatch); w.Write(Ranked);
        }

        public static RoomInfo Read(BinaryReader r) => new RoomInfo
        {
            Code = r.ReadString(), Name = r.ReadString(), HostName = r.ReadString(),
            Players = r.ReadByte(), MaxPlayers = r.ReadByte(), Spectators = r.ReadByte(),
            Locked = r.ReadBoolean(), InMatch = r.ReadBoolean(), Ranked = r.ReadBoolean(),
        };
    }

    /// <summary>One row of the ranked leaderboard.</summary>
    public sealed class LeaderRow
    {
        public int Rank, Rating, Wins, Losses;
        public string Name;

        public void Write(BinaryWriter w) { w.Write(Rank); w.Write(Name ?? ""); w.Write(Rating); w.Write(Wins); w.Write(Losses); }
        public static LeaderRow Read(BinaryReader r) => new LeaderRow { Rank = r.ReadInt32(), Name = r.ReadString(), Rating = r.ReadInt32(), Wins = r.ReadInt32(), Losses = r.ReadInt32() };
    }

    /// <summary>
    /// Length-prefixed framing: int32 little-endian length (type byte + body), then the type byte, then the body.
    /// </summary>
    public static class Wire
    {
        public const int MaxFrame = 256 * 1024; // problem reports carry a compressed log

        public static byte[] Build(byte type, Action<BinaryWriter> body = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(0);
                w.Write(type);
                body?.Invoke(w);
                w.Flush();
                byte[] buf = ms.ToArray();
                int len = buf.Length - 4;
                buf[0] = (byte)len;
                buf[1] = (byte)(len >> 8);
                buf[2] = (byte)(len >> 16);
                buf[3] = (byte)(len >> 24);
                return buf;
            }
        }

        /// <summary>Blocking read of one frame. Returns null on clean EOF.</summary>
        public static byte[] ReadFrame(Stream s)
        {
            byte[] head = new byte[4];
            if (!ReadExact(s, head, 4)) return null;
            int len = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
            if (len <= 0 || len > MaxFrame) throw new IOException("bad frame length " + len);
            byte[] body = new byte[len];
            if (!ReadExact(s, body, len)) throw new IOException("connection closed mid-frame");
            return body;
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0)
                {
                    if (got == 0) return false;
                    throw new IOException("connection closed mid-frame");
                }
                got += n;
            }
            return true;
        }

        public static BinaryReader Reader(byte[] frame, out byte type)
        {
            type = frame[0];
            var r = new BinaryReader(new MemoryStream(frame, 1, frame.Length - 1), Encoding.UTF8);
            return r;
        }

        public static void Tune(TcpClient c)
        {
            c.NoDelay = true;
            c.SendBufferSize = 64 * 1024;
            c.ReceiveBufferSize = 64 * 1024;
        }

        public static void WriteBytes(BinaryWriter w, byte[] data)
        {
            w.Write(data.Length);
            w.Write(data);
        }

        public static byte[] ReadBytes(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxFrame) throw new IOException("bad payload length");
            return r.ReadBytes(n);
        }
    }
}
