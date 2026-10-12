using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Room-based relay and lobby. Clients list public rooms, create rooms (public, public with a password, or private
    /// by code), join them as players or spectators, or ask for a quick match or a ranked match; every Relay frame a
    /// member sends is forwarded to all other members of its room, tagged with the sender's slot. The server knows
    /// almost nothing about the game itself, so the same code runs as a standalone server or inside the game (host mode).
    ///
    /// One port serves raw TCP (direct connections and in-game hosting), WebSocket (for running behind an HTTPS proxy
    /// such as Cloudflare), a JSON status page at /status and the public ranked leaderboard at /leaderboard.
    /// </summary>
    public sealed class RelayServer
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private sealed class Client
        {
            public int Id;
            public TcpClient Tcp;
            public Stream Stream;
            public string Ip = "?";
            public string Name = "?";
            public string ModVersion = "?";
            public PlayerRecord Account;
            public Room Room;
            public int Slot = -1;
            public bool Greeted;
            public long QueuedAt;          // ms on the server clock; 0 = not searching for a ranked match
            public long LastReportAt = -1;
            public readonly object SendLock = new object();
            public int MsgsThisSecond;
            public long SecondStart;

            public bool IsSpectator => Slot >= Msg.FirstSpectatorSlot;
        }

        private sealed class Room
        {
            public string Code;
            public string Name;
            public byte Visibility;
            public string Password = "";
            public bool InMatch, Ranked;
            public int MaxPlayers;
            public int HostSlot;
            public Client[] Slots;
            public readonly Client[] Spectators = new Client[Msg.MaxSpectators];
            public readonly HashSet<string> Banned = new HashSet<string>();
            // ranked bookkeeping for the current match
            public int MatchId;
            public bool MatchPending;
            public Dictionary<int, Client> MatchPlayers = new Dictionary<int, Client>();
            public readonly Dictionary<int, int> Reports = new Dictionary<int, int>();

            public IEnumerable<Client> Players => Slots.Where(s => s != null);
            public IEnumerable<Client> Members => Slots.Concat(Spectators).Where(s => s != null);
            public int PlayerCount => Slots.Count(s => s != null);
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly List<Client> _clients = new List<Client>();
        private readonly Random _rng = new Random();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _nextId = 1;
        private RatingStore _ratings;
        private long _reportWindowStart;
        private int _reportsInWindow;

        public Action<string> Log = _ => { };
        public int Port { get; private set; }

        /// <summary>Shown to players in the lobby browser.</summary>
        public string ServerName = "Digimon Netplay server";
        /// <summary>Trust CF-Connecting-IP / X-Forwarded-For for per-address limits (only when behind a proxy).</summary>
        public bool BehindProxy;
        public int MaxClients = 1000;
        public int MaxClientsPerAddress = 12;
        public int MaxRooms = 300;
        /// <summary>A match sends 60 inputs a second per player; anything far beyond that is abuse.</summary>
        public int MaxMessagesPerSecond = 600;
        public int IdleTimeoutMs = 60000;
        /// <summary>Newest mod version; players on an older one see an update notice.</summary>
        public string LatestVersion = BuildInfo.Version;
        public string DownloadUrl = BuildInfo.ReleasesUrl;
        /// <summary>Where ratings and problem reports are kept. Null = ratings in memory only, no reports.</summary>
        public string DataDir;
        public int MaxReports = 500;
        /// <summary>How fast the ranked search widens, in rating points per second waited (on top of 100).</summary>
        public int QueueWidenPerSecond = 25;

        public RatingStore Ratings => _ratings;

        public void Start(int port, IPAddress bind = null)
        {
            if (DataDir != null)
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(ReportsDir);
            }
            _ratings = new RatingStore(DataDir == null ? null : Path.Combine(DataDir, "ratings.tsv")) { Log = m => Log(m) };
            _listener = new TcpListener(bind ?? IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "relay-accept" };
            _acceptThread.Start();
            new Thread(MatchmakerLoop) { IsBackground = true, Name = "relay-matchmaker" }.Start();
            Log("Relay listening on port " + Port + (DataDir != null ? ", data in " + DataDir + " (" + _ratings.Count + " ranked players)" : ""));
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            lock (_lock)
            {
                foreach (var c in _clients.ToArray()) Drop(c);
            }
        }

        public int RoomCount { get { lock (_lock) return _rooms.Count; } }

        private string ReportsDir => DataDir == null ? null : Path.Combine(DataDir, "reports");
        private long Now => _clock.ElapsedMilliseconds;

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_running) continue; return; }
                Wire.Tune(tcp);
                var c = new Client { Tcp = tcp };
                try { c.Ip = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString(); } catch { }
                lock (_lock)
                {
                    if (_clients.Count >= MaxClients) { try { tcp.Close(); } catch { } continue; }
                    c.Id = _nextId++;
                    _clients.Add(c);
                }
                new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "relay-client-" + c.Id }.Start();
            }
        }

        private void ClientLoop(Client c)
        {
            try
            {
                if (!OpenTransport(c)) return;
                Send(c, Wire.Build(Msg.Welcome, w => { w.Write(c.Id); w.Write(ServerName); w.Write(LatestVersion ?? ""); w.Write(DownloadUrl ?? ""); }));
                while (_running)
                {
                    byte[] frame = Wire.ReadFrame(c.Stream);
                    if (frame == null) break;
                    if (!WithinRate(c)) { Log("client " + c.Id + " (" + c.Ip + ") sent too fast; dropped"); break; }
                    Handle(c, frame);
                    if (c.Greeted && c.Tcp.ReceiveTimeout != IdleTimeoutMs) c.Tcp.ReceiveTimeout = IdleTimeoutMs;
                }
            }
            catch (Exception e)
            {
                if (_running && !(e is IOException) && !(e is ObjectDisposedException)) Log("client " + c.Id + ": " + e.Message);
            }
            finally
            {
                lock (_lock) Drop(c);
            }
        }

        // ------------------------------------------------------------------ transport and web pages

        /// <summary>Works out whether the connection is raw TCP, WebSocket or a web page request. False = done with it.</summary>
        private bool OpenTransport(Client c)
        {
            var net = c.Tcp.GetStream();
            c.Tcp.ReceiveTimeout = 10000; // say something within 10 s
            var first = new byte[4];
            int got = 0;
            while (got < 4)
            {
                int n = net.Read(first, got, 4 - got);
                if (n <= 0) return false;
                got += n;
            }
            if (Encoding.ASCII.GetString(first) != "GET ")
            {
                c.Stream = new PrefixedStream(first, net);
                return CheckAddressLimit(c);
            }

            var head = WsStream.ReadHttpHead(net, first);
            if (head == null) return false;
            if (BehindProxy)
            {
                if (head.Headers.TryGetValue("CF-Connecting-IP", out string ip) && ip.Length > 0) c.Ip = ip;
                else if (head.Headers.TryGetValue("X-Forwarded-For", out string fwd) && fwd.Length > 0) c.Ip = fwd.Split(',')[0].Trim();
            }
            if (head.IsUpgrade)
            {
                c.Stream = WsStream.Accept(net, head);
                return CheckAddressLimit(c);
            }

            string path = (head.Path ?? "/").Split('?')[0].TrimEnd('/').ToLowerInvariant();
            string type = "application/json";
            string body;
            if (path == "/leaderboard.json") body = LeaderboardJson();
            else if (path == "" || path == "/leaderboard") { body = LeaderboardHtml(); type = "text/html; charset=utf-8"; }
            else
            {
                lock (_lock)
                    body = "{\"server\":" + Json(ServerName) + ",\"protocol\":" + Msg.ProtocolVersion + ",\"version\":" + Json(LatestVersion) +
                           ",\"players\":" + _clients.Count(x => x.Greeted) + ",\"rooms\":" + _rooms.Count +
                           ",\"searchingRanked\":" + _clients.Count(x => x.QueuedAt > 0) + "}";
            }
            byte[] b = Encoding.UTF8.GetBytes(body);
            string resp = "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache\r\nContent-Length: " + b.Length +
                          "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(resp);
            net.Write(h, 0, h.Length);
            net.Write(b, 0, b.Length);
            return false;
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20 || ch == '<' || ch == '>' || ch == '&') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        private static string Html(string s) => WebUtility.HtmlEncode(s ?? "");

        private string LeaderboardJson()
        {
            var rows = _ratings.Top(100);
            var sb = new StringBuilder("{\"server\":" + Json(ServerName) + ",\"players\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"rank\":").Append(r.Rank).Append(",\"name\":").Append(Json(r.Name)).Append(",\"rating\":").Append(r.Rating)
                  .Append(",\"wins\":").Append(r.Wins).Append(",\"losses\":").Append(r.Losses).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private string LeaderboardHtml()
        {
            var rows = _ratings.Top(100);
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<meta http-equiv=\"refresh\" content=\"60\"><title>Ranked leaderboard</title><style>");
            sb.Append(":root{--bg:#f4f6fb;--card:#fff;--text:#121a2f;--muted:#5b6582;--line:#e1e6f0;--accent:#b36b00}");
            sb.Append("@media (prefers-color-scheme:dark){:root{--bg:#070d1f;--card:#0f1a36;--text:#eef2ff;--muted:#9aa6c8;--line:#1f2c52;--accent:#ffc640}}");
            sb.Append("body{margin:0;background:var(--bg);color:var(--text);font:16px/1.4 system-ui,sans-serif}main{max-width:760px;margin:0 auto;padding:24px 16px}");
            sb.Append("h1{margin:0 0 4px;font-size:28px}p{color:var(--muted);margin:0 0 20px}table{width:100%;border-collapse:collapse;background:var(--card);border-radius:10px;overflow:hidden}");
            sb.Append("th,td{padding:10px 12px;text-align:left;border-bottom:1px solid var(--line)}th{color:var(--muted);font-weight:600;font-size:13px;text-transform:uppercase}");
            sb.Append("td.n{text-align:right;font-variant-numeric:tabular-nums}th.n{text-align:right}tbody tr:first-child td{color:var(--accent);font-weight:700}.empty{padding:24px;text-align:center;color:var(--muted)}");
            sb.Append("</style></head><body><main><h1>Ranked leaderboard</h1><p>").Append(Html(ServerName)).Append(" &middot; Project Digimon online versus &middot; Elo ratings, everyone starts at ")
              .Append(RatingStore.StartRating).Append("</p><table><thead><tr><th class=\"n\">#</th><th>Player</th><th class=\"n\">Rating</th><th class=\"n\">Wins</th><th class=\"n\">Losses</th></tr></thead><tbody>");
            foreach (var r in rows)
                sb.Append("<tr><td class=\"n\">").Append(r.Rank).Append("</td><td>").Append(Html(r.Name)).Append("</td><td class=\"n\">").Append(r.Rating)
                  .Append("</td><td class=\"n\">").Append(r.Wins).Append("</td><td class=\"n\">").Append(r.Losses).Append("</td></tr>");
            if (rows.Count == 0) sb.Append("<tr><td colspan=\"5\" class=\"empty\">No ranked games played yet.</td></tr>");
            sb.Append("</tbody></table></main></body></html>");
            return sb.ToString();
        }

        private bool CheckAddressLimit(Client c)
        {
            lock (_lock)
            {
                if (IPAddress.TryParse(c.Ip, out var addr) && IPAddress.IsLoopback(addr)) return true; // in-game host, local tunnel
                if (_clients.Count(x => x.Ip == c.Ip) <= MaxClientsPerAddress) return true;
            }
            SendError(c, "Too many connections from your address.");
            return false;
        }

        private bool WithinRate(Client c)
        {
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            if (now != c.SecondStart) { c.SecondStart = now; c.MsgsThisSecond = 0; }
            return ++c.MsgsThisSecond <= MaxMessagesPerSecond;
        }

        private static bool More(BinaryReader r) => r.BaseStream.Position < r.BaseStream.Length;

        // ------------------------------------------------------------------ messages

        private void Handle(Client c, byte[] frame)
        {
            using (var r = Wire.Reader(frame, out byte type))
            {
                if (!c.Greeted && type != Msg.Hello && type != Msg.Ping)
                {
                    SendError(c, "Say hello first.");
                    return;
                }
                switch (type)
                {
                    case Msg.Hello:
                    {
                        int proto = r.ReadInt32();
                        if (proto != Msg.ProtocolVersion)
                        {
                            SendError(c, "Version mismatch: this server needs mod version " + LatestVersion + " (protocol " + Msg.ProtocolVersion +
                                         "), you have protocol " + proto + ". Update the mod: " + DownloadUrl);
                            return;
                        }
                        c.Name = Sanitize(r.ReadString());
                        if (More(r)) c.ModVersion = Clip(r.ReadString(), 20);
                        string token = More(r) ? r.ReadString() : "";
                        if (token.Length >= 16 && token.Length <= 128) c.Account = _ratings.Get(RatingStore.KeyOf(token), c.Name);
                        c.Greeted = true;
                        if (c.Account != null) SendAccount(c);
                        break;
                    }
                    case Msg.Ping:
                    {
                        long ticks = r.ReadInt64();
                        Send(c, Wire.Build(Msg.Pong, w => w.Write(ticks)));
                        break;
                    }
                    case Msg.Create:
                    {
                        int max = Math.Max(2, Math.Min(4, (int)r.ReadByte()));
                        string roomName = Clip(r.ReadString(), 32);
                        byte visibility = r.ReadByte() == RoomVisibility.Private ? RoomVisibility.Private : RoomVisibility.Public;
                        string password = Clip(r.ReadString(), 32);
                        lock (_lock) CreateRoom(c, max, roomName, visibility, password, false);
                        break;
                    }
                    case Msg.Join:
                    {
                        string code = (r.ReadString() ?? "").Trim().ToUpperInvariant();
                        string password = Clip(r.ReadString(), 32);
                        bool spectate = More(r) && r.ReadBoolean();
                        lock (_lock) JoinRoom(c, code, password, spectate);
                        break;
                    }
                    case Msg.QuickMatch:
                        lock (_lock) QuickMatch(c);
                        break;
                    case Msg.Leave:
                        lock (_lock) LeaveRoom(c);
                        break;
                    case Msg.ListRooms:
                        SendRoomList(c);
                        break;
                    case Msg.SetInMatch:
                    {
                        bool inMatch = r.ReadBoolean();
                        int matchId = More(r) ? r.ReadInt32() : 0;
                        lock (_lock) SetInMatch(c, inMatch, matchId);
                        break;
                    }
                    case Msg.Kick:
                    {
                        int slot = r.ReadByte();
                        lock (_lock) Kick(c, slot);
                        break;
                    }
                    case Msg.UpdateRoom:
                    {
                        int max = Math.Max(2, Math.Min(4, (int)r.ReadByte()));
                        string roomName = Clip(r.ReadString(), 32);
                        byte visibility = r.ReadByte() == RoomVisibility.Private ? RoomVisibility.Private : RoomVisibility.Public;
                        bool changePassword = r.ReadBoolean();
                        string password = Clip(r.ReadString(), 32);
                        lock (_lock) UpdateRoom(c, max, roomName, visibility, changePassword, password);
                        break;
                    }
                    case Msg.Report:
                    {
                        string description = Clip(r.ReadString(), 500);
                        string version = Clip(r.ReadString(), 20);
                        byte[] log = Wire.ReadBytes(r);
                        SaveReport(c, description, version, log);
                        break;
                    }
                    case Msg.QueueRanked:
                        lock (_lock)
                        {
                            if (c.Account == null) { SendError(c, "Ranked play needs mod version 0.5.0 or newer."); return; }
                            LeaveRoom(c);
                            if (c.QueuedAt == 0) c.QueuedAt = Math.Max(1, Now);
                            SendQueueStatus(c);
                        }
                        break;
                    case Msg.CancelQueue:
                        lock (_lock)
                        {
                            c.QueuedAt = 0;
                            SendQueueStatus(c);
                        }
                        break;
                    case Msg.MatchResult:
                    {
                        int matchId = r.ReadInt32();
                        int winner = r.ReadByte();
                        lock (_lock) OnMatchResult(c, matchId, winner);
                        break;
                    }
                    case Msg.GetLeaderboard:
                    {
                        var rows = _ratings.Top(100);
                        Send(c, Wire.Build(Msg.Leaderboard, w => { w.Write(rows.Count); foreach (var row in rows) row.Write(w); }));
                        if (c.Account != null) SendAccount(c);
                        break;
                    }
                    case Msg.Relay:
                    case Msg.RelaySpectators:
                    {
                        byte[] payload = Wire.ReadBytes(r);
                        Client[] targets;
                        int from;
                        lock (_lock)
                        {
                            if (c.Room == null) return;
                            from = c.Slot;
                            targets = (type == Msg.Relay ? c.Room.Members : c.Room.Spectators.Where(s => s != null)).ToArray();
                        }
                        byte[] outFrame = Wire.Build(Msg.Relayed, w => { w.Write((byte)from); Wire.WriteBytes(w, payload); });
                        foreach (var t in targets)
                            if (t != c) Send(t, outFrame);
                        break;
                    }
                }
            }
        }

        private void SendRoomList(Client c)
        {
            RoomInfo[] list;
            int online;
            lock (_lock)
            {
                online = _clients.Count(x => x.Greeted);
                list = _rooms.Values
                    .Where(x => x.Visibility == RoomVisibility.Public)
                    .Select(x => new RoomInfo
                    {
                        Code = x.Code, Name = x.Name, HostName = x.Slots[x.HostSlot]?.Name ?? "?",
                        Players = x.PlayerCount, MaxPlayers = x.MaxPlayers, Spectators = x.Spectators.Count(m => m != null),
                        Locked = x.Password.Length > 0, InMatch = x.InMatch, Ranked = x.Ranked,
                    })
                    .OrderBy(x => x.Ranked).ThenBy(x => x.InMatch || x.Players >= x.MaxPlayers).ThenBy(x => x.Locked).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(100).ToArray();
            }
            Send(c, Wire.Build(Msg.RoomList, w =>
            {
                w.Write(online);
                w.Write(list.Length);
                foreach (var info in list) info.Write(w);
            }));
        }

        // ------------------------------------------------------------------ rooms (callers hold _lock)

        private Room CreateRoom(Client c, int max, string roomName, byte visibility, string password, bool ranked)
        {
            if (_rooms.Count >= MaxRooms)
            {
                SendError(c, "The server is full right now. Try again later.");
                return null;
            }
            LeaveRoom(c);
            c.QueuedAt = 0;
            var room = new Room
            {
                Code = NewCode(), Name = roomName.Length > 0 ? roomName : c.Name + "'s room", Visibility = visibility,
                Password = password, MaxPlayers = max, HostSlot = 0, Slots = new Client[max], Ranked = ranked,
            };
            _rooms[room.Code] = room;
            Seat(c, room, 0);
            Log("Room " + room.Code + " (" + (ranked ? "ranked" : visibility == RoomVisibility.Private ? "private" : password.Length > 0 ? "password" : "public") + ") created by " + c.Name);
            return room;
        }

        private void JoinRoom(Client c, string code, string password, bool spectate)
        {
            if (!_rooms.TryGetValue(code, out Room room))
            {
                SendError(c, "No room with code " + code + ".");
                return;
            }
            if (c.Room == room && c.IsSpectator == spectate) return;
            if (room.Banned.Contains("id:" + c.Id) || (c.Account != null && room.Banned.Contains("acct:" + c.Account.Key)))
            {
                SendError(c, "The host removed you from " + room.Name + ".");
                return;
            }
            if (room.Password.Length > 0 && password != room.Password)
            {
                SendError(c, password.Length == 0 ? "Room " + room.Name + " needs a password." : "Wrong password for " + room.Name + ".");
                return;
            }
            int slot;
            if (spectate)
            {
                int i = Array.FindIndex(room.Spectators, s => s == null);
                if (i < 0)
                {
                    SendError(c, "Room " + room.Name + " has no spectator seats left.");
                    return;
                }
                slot = Msg.FirstSpectatorSlot + i;
            }
            else
            {
                if (room.Ranked)
                {
                    SendError(c, "Ranked rooms are matched by the server. You can watch instead.");
                    return;
                }
                if (room.InMatch)
                {
                    SendError(c, "Room " + room.Name + " is in a match. Try again when it ends, or watch.");
                    return;
                }
                slot = Array.FindIndex(room.Slots, s => s == null);
                if (slot < 0)
                {
                    SendError(c, "Room " + room.Name + " is full.");
                    return;
                }
            }
            LeaveRoom(c);
            c.QueuedAt = 0;
            Seat(c, room, slot);
            Log(c.Name + " joined room " + code + (spectate ? " as a spectator" : " as slot " + slot));
        }

        private void QuickMatch(Client c)
        {
            var room = _rooms.Values
                .Where(x => x != c.Room && x.Visibility == RoomVisibility.Public && !x.Ranked && x.Password.Length == 0 && !x.InMatch &&
                            x.PlayerCount < x.MaxPlayers && !x.Banned.Contains("id:" + c.Id) && !(c.Account != null && x.Banned.Contains("acct:" + c.Account.Key)))
                .OrderBy(x => x.MaxPlayers == 2 && x.PlayerCount == 1 ? 0 : 1)
                .ThenByDescending(x => x.PlayerCount)
                .FirstOrDefault();
            if (room != null) JoinRoom(c, room.Code, "", false);
            else CreateRoom(c, 2, c.Name + "'s quick match", RoomVisibility.Public, "", false);
        }

        private void Seat(Client c, Room room, int slot)
        {
            if (slot >= Msg.FirstSpectatorSlot) room.Spectators[slot - Msg.FirstSpectatorSlot] = c;
            else room.Slots[slot] = c;
            c.Room = room;
            c.Slot = slot;
            Send(c, Wire.Build(Msg.RoomJoined, w =>
            {
                w.Write(room.Code); w.Write((byte)slot); w.Write((byte)room.HostSlot); w.Write((byte)room.MaxPlayers);
                w.Write(room.Name); w.Write(room.Visibility); w.Write(room.Password.Length > 0); w.Write(room.Ranked);
            }));
            foreach (var other in room.Members)
            {
                if (other == c) continue;
                var o = other;
                Send(c, Wire.Build(Msg.PeerJoined, w => { w.Write((byte)o.Slot); w.Write(o.Name); w.Write(PublicRating(o)); }));
                Send(o, Wire.Build(Msg.PeerJoined, w => { w.Write((byte)slot); w.Write(c.Name); w.Write(PublicRating(c)); }));
            }
        }

        private static int PublicRating(Client c) => c.Account != null && c.Account.Games > 0 ? c.Account.Rating : 0;

        private void LeaveRoom(Client c)
        {
            var room = c.Room;
            if (room == null) return;
            int slot = c.Slot;
            bool spectator = c.IsSpectator;
            if (spectator) room.Spectators[slot - Msg.FirstSpectatorSlot] = null;
            else room.Slots[slot] = null;
            c.Room = null;
            c.Slot = -1;

            if (!spectator && room.Ranked && room.MatchPending && room.MatchPlayers.ContainsKey(slot)) RankedPlayerLeft(room, slot);
            if (!spectator && room.InMatch) room.InMatch = false; // the match aborts for everyone left

            foreach (var other in room.Members) Send(other, Wire.Build(Msg.PeerLeft, w => w.Write((byte)slot)));

            if (room.PlayerCount == 0)
            {
                foreach (var s in room.Members.ToArray())
                {
                    SendRoomLeft(s, "The room closed.");
                    s.Room = null;
                    s.Slot = -1;
                }
                _rooms.Remove(room.Code);
                Log("Room " + room.Code + " closed");
                return;
            }
            if (!spectator && slot == room.HostSlot)
            {
                room.HostSlot = room.Players.Min(p => p.Slot);
                foreach (var other in room.Members) Send(other, Wire.Build(Msg.HostChanged, w => w.Write((byte)room.HostSlot)));
                Log("Room " + room.Code + ": " + room.Slots[room.HostSlot].Name + " is the new host");
            }
        }

        private void SetInMatch(Client c, bool inMatch, int matchId)
        {
            var room = c.Room;
            if (room == null || c.Slot != room.HostSlot) return;
            room.InMatch = inMatch;
            if (!inMatch || !room.Ranked) return;
            var players = room.Players.ToList();
            if (players.Count != 2 || players.Any(p => p.Account == null)) return;
            room.MatchId = matchId;
            room.MatchPending = true;
            room.MatchPlayers = players.ToDictionary(p => p.Slot);
            room.Reports.Clear();
        }

        private void Kick(Client c, int slot)
        {
            var room = c.Room;
            if (room == null || c.Slot != room.HostSlot || slot == c.Slot) return;
            if (room.Ranked) { SendError(c, "Nobody can be removed from a ranked room."); return; }
            Client target = slot >= Msg.FirstSpectatorSlot
                ? (slot - Msg.FirstSpectatorSlot < Msg.MaxSpectators ? room.Spectators[slot - Msg.FirstSpectatorSlot] : null)
                : (slot < room.Slots.Length ? room.Slots[slot] : null);
            if (target == null) return;
            room.Banned.Add("id:" + target.Id);
            if (target.Account != null) room.Banned.Add("acct:" + target.Account.Key);
            LeaveRoom(target);
            SendRoomLeft(target, "The host removed you from the room.");
            Log("Room " + room.Code + ": " + c.Name + " removed " + target.Name);
        }

        private void UpdateRoom(Client c, int max, string roomName, byte visibility, bool changePassword, string password)
        {
            var room = c.Room;
            if (room == null || c.Slot != room.HostSlot) return;
            if (room.Ranked) { SendError(c, "Ranked rooms cannot be changed."); return; }
            if (room.InMatch) { SendError(c, "Wait for the match to end."); return; }
            if (room.PlayerCount > max) { SendError(c, "There are too many players in the room for " + (max == 2 ? "1 vs 1." : max + " players.")); return; }
            if (roomName.Length > 0) room.Name = roomName;
            room.Visibility = visibility;
            if (changePassword) room.Password = password;

            if (max != room.MaxPlayers)
            {
                var players = room.Players.ToList();
                var slots = new Client[max];
                var moved = new List<KeyValuePair<Client, int>>();
                foreach (var p in players) if (p.Slot < max) slots[p.Slot] = p;
                foreach (var p in players)
                {
                    if (p.Slot < max) continue;
                    int free = Array.FindIndex(slots, s => s == null);
                    slots[free] = p;
                    moved.Add(new KeyValuePair<Client, int>(p, free));
                }
                room.Slots = slots;
                room.MaxPlayers = max;
                foreach (var kv in moved)
                {
                    var p = kv.Key;
                    int old = p.Slot;
                    if (old == room.HostSlot) room.HostSlot = kv.Value;
                    foreach (var other in room.Members) if (other != p) Send(other, Wire.Build(Msg.PeerLeft, w => w.Write((byte)old)));
                    room.Slots[kv.Value] = null; // Seat puts them back
                    Seat(p, room, kv.Value);
                }
                if (moved.Count > 0)
                    foreach (var other in room.Members) Send(other, Wire.Build(Msg.HostChanged, w => w.Write((byte)room.HostSlot)));
            }
            foreach (var m in room.Members)
                Send(m, Wire.Build(Msg.RoomUpdated, w => { w.Write((byte)room.MaxPlayers); w.Write(room.Name); w.Write(room.Visibility); w.Write(room.Password.Length > 0); }));
        }

        private void Drop(Client c)
        {
            LeaveRoom(c);
            c.QueuedAt = 0;
            _clients.Remove(c);
            try { c.Tcp.Close(); } catch { }
        }

        // ------------------------------------------------------------------ ranked (callers hold _lock)

        private void MatchmakerLoop()
        {
            long lastStatus = 0;
            while (_running)
            {
                Thread.Sleep(250);
                lock (_lock)
                {
                    long now = Now;
                    var queue = _clients.Where(x => x.QueuedAt > 0 && x.Account != null && x.Room == null).OrderBy(x => x.QueuedAt).ToList();
                    var matched = new HashSet<Client>();
                    foreach (var a in queue)
                    {
                        if (matched.Contains(a)) continue;
                        Client best = null;
                        int bestDiff = int.MaxValue;
                        foreach (var b in queue)
                        {
                            if (b == a || matched.Contains(b) || b.Account.Key == a.Account.Key) continue;
                            long waited = Math.Max(now - a.QueuedAt, now - b.QueuedAt) / 1000;
                            long window = 100 + QueueWidenPerSecond * waited;
                            int diff = Math.Abs(a.Account.Rating - b.Account.Rating);
                            if (diff <= window && diff < bestDiff) { best = b; bestDiff = diff; }
                        }
                        if (best == null) continue;
                        matched.Add(a);
                        matched.Add(best);
                        StartRanked(a, best);
                    }
                    if (now - lastStatus >= 1000)
                    {
                        lastStatus = now;
                        foreach (var c in queue) if (!matched.Contains(c)) SendQueueStatus(c);
                    }
                }
            }
        }

        private void StartRanked(Client a, Client b)
        {
            a.QueuedAt = b.QueuedAt = 0;
            SendQueueStatus(a);
            SendQueueStatus(b);
            var room = CreateRoom(a, 2, "Ranked: " + a.Name + " vs " + b.Name, RoomVisibility.Public, "", true);
            if (room == null) return;
            Seat(b, room, 1);
            Log("Ranked match " + room.Code + ": " + a.Account.Display + " (" + a.Account.Rating + ") vs " + b.Account.Display + " (" + b.Account.Rating + ")");
        }

        private void SendQueueStatus(Client c)
        {
            int searching = _clients.Count(x => x.QueuedAt > 0);
            bool on = c.QueuedAt > 0;
            int secs = on ? (int)((Now - c.QueuedAt) / 1000) : 0;
            Send(c, Wire.Build(Msg.QueueStatus, w => { w.Write(on); w.Write(secs); w.Write(searching); }));
        }

        private void SendAccount(Client c)
        {
            var a = c.Account;
            int rank = _ratings.RankOf(a);
            Send(c, Wire.Build(Msg.AccountInfo, w => { w.Write(a.Display); w.Write(a.Rating); w.Write(a.Wins); w.Write(a.Losses); w.Write(rank); }));
        }

        private void OnMatchResult(Client c, int matchId, int winner)
        {
            var room = c.Room;
            if (room == null || !room.Ranked || !room.MatchPending || room.MatchId != matchId || !room.MatchPlayers.ContainsKey(c.Slot)) return;
            room.Reports[c.Slot] = winner;
            int other = room.MatchPlayers.Keys.First(s => s != c.Slot);
            if (room.Reports.TryGetValue(other, out int theirs))
            {
                if (theirs == winner) FinishRanked(room, winner);
                else
                {
                    room.MatchPending = false;
                    Log("Ranked match in " + room.Code + ": players reported different results; no rating change");
                    foreach (var p in room.MatchPlayers.Values) SendError(p, "The two games saw different results, so ratings did not change.");
                }
            }
            else if (room.Slots[other] != room.MatchPlayers[other])
                FinishRanked(room, winner); // the opponent already left without contesting
        }

        private void RankedPlayerLeft(Room room, int slot)
        {
            int other = room.MatchPlayers.Keys.First(s => s != slot);
            if (room.Reports.TryGetValue(other, out int reported)) FinishRanked(room, reported);
            else if (room.InMatch && !room.Reports.ContainsKey(slot)) FinishRanked(room, other); // left mid-match: forfeit
            // Otherwise the match is over and the one who stayed has not reported yet; their report settles it.
        }

        private void FinishRanked(Room room, int winnerSlot)
        {
            room.MatchPending = false;
            var slots = room.MatchPlayers.Keys.OrderBy(s => s).ToArray();
            var a = room.MatchPlayers[slots[0]];
            var b = room.MatchPlayers[slots[1]];
            int oldA = a.Account.Rating, oldB = b.Account.Rating;
            double score = winnerSlot == slots[0] ? 1 : winnerSlot == slots[1] ? 0 : 0.5;
            _ratings.Apply(a.Account, b.Account, score);
            Log("Ranked result " + room.Code + ": " + a.Account.Display + " " + oldA + "->" + a.Account.Rating + ", " + b.Account.Display + " " + oldB + "->" + b.Account.Rating);
            int resA = score > 0.75 ? 1 : score < 0.25 ? 0 : 2;
            int resB = resA == 2 ? 2 : 1 - resA;
            SendRating(a, oldA, resA, b);
            SendRating(b, oldB, resB, a);
        }

        private void SendRating(Client me, int old, int result, Client opponent)
        {
            Send(me, Wire.Build(Msg.RatingChanged, w => { w.Write(old); w.Write(me.Account.Rating); w.Write((byte)result); w.Write(opponent.Account.Display); }));
            SendAccount(me);
        }

        // ------------------------------------------------------------------ problem reports

        private void SaveReport(Client c, string description, string version, byte[] gz)
        {
            string dir = ReportsDir;
            if (dir == null) { SendError(c, "This server does not collect problem reports."); return; }
            long now = Now;
            lock (_lock)
            {
                if (c.LastReportAt >= 0 && now - c.LastReportAt < 60000) { SendError(c, "Please wait a minute before sending another report."); return; }
                if (now - _reportWindowStart > 60000) { _reportWindowStart = now; _reportsInWindow = 0; }
                if (++_reportsInWindow > 20) { SendError(c, "Too many reports right now. Try again in a minute."); return; }
                c.LastReportAt = now;
            }
            try
            {
                if (Directory.GetFiles(dir, "*.txt").Length >= MaxReports) { SendError(c, "The server's report box is full. Tell the server owner."); return; }
                string log = Gunzip(gz, 4 * 1024 * 1024);
                string id;
                lock (_lock) id = NewCode() + _rng.Next(10);
                var sb = new StringBuilder();
                sb.Append("Report ").Append(id).Append('\n');
                sb.Append("Received: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n");
                sb.Append("Player: ").Append(c.Account != null ? c.Account.Display : c.Name).Append('\n');
                sb.Append("Mod version: ").Append(version).Append('\n');
                sb.Append("Description: ").Append(description).Append("\n\n----- log -----\n").Append(log);
                File.WriteAllText(Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id + ".txt"), sb.ToString());
                Log("Problem report " + id + " from " + c.Name + " (" + log.Length / 1024 + " KB of log)");
                Send(c, Wire.Build(Msg.ReportSaved, w => w.Write(id)));
            }
            catch (Exception e)
            {
                Log("Could not save a problem report: " + e.Message);
                SendError(c, "The server could not save the report.");
            }
        }

        private static string Gunzip(byte[] gz, int limit)
        {
            using (var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while (outp.Length < limit && (n = z.Read(buf, 0, buf.Length)) > 0) outp.Write(buf, 0, n);
                return Encoding.UTF8.GetString(outp.ToArray());
            }
        }

        // ------------------------------------------------------------------ helpers

        private void SendError(Client c, string text) => Send(c, Wire.Build(Msg.Error, w => w.Write(text)));
        private void SendRoomLeft(Client c, string reason) => Send(c, Wire.Build(Msg.RoomLeft, w => w.Write(reason)));

        private void Send(Client c, byte[] frame)
        {
            try
            {
                lock (c.SendLock) c.Stream.Write(frame, 0, frame.Length);
            }
            catch
            {
                try { c.Tcp.Close(); } catch { }
            }
        }

        private string NewCode()
        {
            string code;
            do
            {
                var chars = new char[5];
                for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[_rng.Next(CodeAlphabet.Length)];
                code = new string(chars);
            } while (_rooms.ContainsKey(code));
            return code;
        }

        private static string Sanitize(string name)
        {
            name = Clip(name, 20).Replace("#", "");
            return name.Length == 0 ? "Tamer" : name;
        }

        private static string Clip(string text, int max)
        {
            var sb = new StringBuilder();
            foreach (char ch in (text ?? "").Trim())
                if (!char.IsControl(ch) && sb.Length < max) sb.Append(ch);
            return sb.ToString().Trim();
        }
    }
}
