using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Room-based relay and lobby. Clients list public rooms, create rooms (public, public with a password, or private
    /// by code) and join them; every Relay frame a member sends is forwarded to all other members of its room, tagged
    /// with the sender's slot. The server knows nothing about the game itself, so the same code runs as a standalone
    /// server or inside the game (host mode).
    ///
    /// One port serves three things: raw TCP (direct connections and in-game hosting), WebSocket (for running behind
    /// an HTTPS proxy such as Cloudflare) and a plain HTTP status page at /status.
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
            public Room Room;
            public int Slot = -1;
            public bool Greeted;
            public readonly object SendLock = new object();
            public int MsgsThisSecond;
            public long SecondStart;
        }

        private sealed class Room
        {
            public string Code;
            public string Name;
            public byte Visibility;
            public string Password = "";
            public bool InMatch;
            public int MaxPlayers;
            public int HostSlot;
            public Client[] Slots;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly List<Client> _clients = new List<Client>();
        private readonly Random _rng = new Random();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _nextId = 1;

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

        public void Start(int port, IPAddress bind = null)
        {
            _listener = new TcpListener(bind ?? IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "relay-accept" };
            _acceptThread.Start();
            Log("Relay listening on port " + Port);
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
                Send(c, Wire.Build(Msg.Welcome, w => { w.Write(c.Id); w.Write(ServerName); }));
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

        /// <summary>Works out whether the connection is raw TCP, WebSocket or a status request. False = done with it.</summary>
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

            string body;
            lock (_lock)
                body = "{\"server\":\"" + ServerName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"protocol\":" + Msg.ProtocolVersion +
                       ",\"players\":" + _clients.Count(x => x.Greeted) + ",\"rooms\":" + _rooms.Count + "}";
            byte[] b = Encoding.UTF8.GetBytes(body);
            string resp = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: " + b.Length +
                          "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(resp);
            net.Write(h, 0, h.Length);
            net.Write(b, 0, b.Length);
            return false;
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
                        string name = r.ReadString();
                        if (proto != Msg.ProtocolVersion)
                        {
                            SendError(c, "Version mismatch: server speaks protocol " + Msg.ProtocolVersion + ", you sent " + proto + ". Update the mod.");
                            return;
                        }
                        c.Name = Sanitize(name);
                        c.Greeted = true;
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
                        lock (_lock)
                        {
                            if (_rooms.Count >= MaxRooms)
                            {
                                SendError(c, "The server is full right now. Try again later.");
                                return;
                            }
                            LeaveRoom(c);
                            var room = new Room
                            {
                                Code = NewCode(), Name = roomName.Length > 0 ? roomName : c.Name + "'s room", Visibility = visibility,
                                Password = password, MaxPlayers = max, HostSlot = 0, Slots = new Client[max],
                            };
                            _rooms[room.Code] = room;
                            Seat(c, room, 0);
                            Log("Room " + room.Code + " (" + (visibility == RoomVisibility.Private ? "private" : password.Length > 0 ? "password" : "public") + ") created by " + c.Name);
                        }
                        break;
                    }
                    case Msg.Join:
                    {
                        string code = (r.ReadString() ?? "").Trim().ToUpperInvariant();
                        string password = Clip(r.ReadString(), 32);
                        lock (_lock)
                        {
                            if (!_rooms.TryGetValue(code, out Room room))
                            {
                                SendError(c, "No room with code " + code + ".");
                                return;
                            }
                            if (c.Room == room) return;
                            if (room.Password.Length > 0 && password != room.Password)
                            {
                                SendError(c, password.Length == 0 ? "Room " + room.Name + " needs a password." : "Wrong password for " + room.Name + ".");
                                return;
                            }
                            if (room.InMatch)
                            {
                                SendError(c, "Room " + room.Name + " is in a match. Try again when it ends.");
                                return;
                            }
                            int slot = Array.FindIndex(room.Slots, s => s == null);
                            if (slot < 0)
                            {
                                SendError(c, "Room " + code + " is full.");
                                return;
                            }
                            LeaveRoom(c);
                            Seat(c, room, slot);
                            Log(c.Name + " joined room " + code + " as slot " + slot);
                        }
                        break;
                    }
                    case Msg.Leave:
                        lock (_lock) LeaveRoom(c);
                        break;
                    case Msg.ListRooms:
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
                                    Players = x.Slots.Count(m => m != null), MaxPlayers = x.MaxPlayers,
                                    Locked = x.Password.Length > 0, InMatch = x.InMatch,
                                })
                                .OrderBy(x => x.InMatch || x.Players >= x.MaxPlayers).ThenBy(x => x.Locked).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                                .Take(100).ToArray();
                        }
                        Send(c, Wire.Build(Msg.RoomList, w =>
                        {
                            w.Write(online);
                            w.Write(list.Length);
                            foreach (var info in list) info.Write(w);
                        }));
                        break;
                    }
                    case Msg.SetInMatch:
                    {
                        bool inMatch = r.ReadBoolean();
                        lock (_lock)
                            if (c.Room != null && c.Slot == c.Room.HostSlot) c.Room.InMatch = inMatch;
                        break;
                    }
                    case Msg.Relay:
                    {
                        byte[] payload = Wire.ReadBytes(r);
                        Client[] targets;
                        int from;
                        lock (_lock)
                        {
                            if (c.Room == null) return;
                            from = c.Slot;
                            targets = (Client[])c.Room.Slots.Clone();
                        }
                        byte[] outFrame = Wire.Build(Msg.Relayed, w => { w.Write((byte)from); Wire.WriteBytes(w, payload); });
                        foreach (var t in targets)
                            if (t != null && t != c) Send(t, outFrame);
                        break;
                    }
                }
            }
        }

        // Callers hold _lock.
        private void Seat(Client c, Room room, int slot)
        {
            room.Slots[slot] = c;
            c.Room = room;
            c.Slot = slot;
            Send(c, Wire.Build(Msg.RoomJoined, w =>
            {
                w.Write(room.Code); w.Write((byte)slot); w.Write((byte)room.HostSlot); w.Write((byte)room.MaxPlayers);
                w.Write(room.Name); w.Write(room.Visibility); w.Write(room.Password.Length > 0);
            }));
            for (int i = 0; i < room.Slots.Length; i++)
            {
                var other = room.Slots[i];
                if (other == null || other == c) continue;
                int otherSlot = i;
                Send(c, Wire.Build(Msg.PeerJoined, w => { w.Write((byte)otherSlot); w.Write(other.Name); }));
                Send(other, Wire.Build(Msg.PeerJoined, w => { w.Write((byte)slot); w.Write(c.Name); }));
            }
        }

        // Callers hold _lock.
        private void LeaveRoom(Client c)
        {
            var room = c.Room;
            if (room == null) return;
            int slot = c.Slot;
            room.Slots[slot] = null;
            c.Room = null;
            c.Slot = -1;
            bool hostLeft = slot == room.HostSlot;
            foreach (var other in room.Slots)
            {
                if (other == null) continue;
                Send(other, Wire.Build(Msg.PeerLeft, w => w.Write((byte)slot)));
                if (hostLeft)
                {
                    SendError(other, "The host closed the room.");
                    other.Room = null;
                    other.Slot = -1;
                }
            }
            if (hostLeft || Array.TrueForAll(room.Slots, s => s == null))
            {
                _rooms.Remove(room.Code);
                Log("Room " + room.Code + " closed");
            }
        }

        // Callers hold _lock.
        private void Drop(Client c)
        {
            LeaveRoom(c);
            _clients.Remove(c);
            try { c.Tcp.Close(); } catch { }
        }

        private void SendError(Client c, string text) => Send(c, Wire.Build(Msg.Error, w => w.Write(text)));

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
            name = Clip(name, 20);
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
