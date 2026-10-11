using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Room-based TCP relay. Clients create or join a room by code; every Relay frame a member sends is
    /// forwarded to all other members of its room, tagged with the sender's slot. The server knows nothing
    /// about the game itself, so the same code runs as a standalone server or inside the game (host mode).
    /// </summary>
    public sealed class RelayServer
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private sealed class Client
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public string Name = "?";
            public Room Room;
            public int Slot = -1;
            public bool Greeted;
            public readonly object SendLock = new object();
        }

        private sealed class Room
        {
            public string Code;
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
                var c = new Client { Tcp = tcp, Stream = tcp.GetStream() };
                lock (_lock) { c.Id = _nextId++; _clients.Add(c); }
                new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "relay-client-" + c.Id }.Start();
            }
        }

        private void ClientLoop(Client c)
        {
            try
            {
                Send(c, Wire.Build(Msg.Welcome, w => w.Write(c.Id)));
                while (_running)
                {
                    byte[] frame = Wire.ReadFrame(c.Stream);
                    if (frame == null) break;
                    Handle(c, frame);
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
                        lock (_lock)
                        {
                            LeaveRoom(c);
                            var room = new Room { Code = NewCode(), MaxPlayers = max, HostSlot = 0, Slots = new Client[max] };
                            _rooms[room.Code] = room;
                            Seat(c, room, 0);
                            Log("Room " + room.Code + " created by " + c.Name);
                        }
                        break;
                    }
                    case Msg.Join:
                    {
                        string code = (r.ReadString() ?? "").Trim().ToUpperInvariant();
                        lock (_lock)
                        {
                            if (!_rooms.TryGetValue(code, out Room room))
                            {
                                SendError(c, "No room with code " + code + ".");
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
            Send(c, Wire.Build(Msg.RoomJoined, w => { w.Write(room.Code); w.Write((byte)slot); w.Write((byte)room.HostSlot); w.Write((byte)room.MaxPlayers); }));
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
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "Tamer";
            if (name.Length > 20) name = name.Substring(0, 20);
            return name;
        }
    }
}
