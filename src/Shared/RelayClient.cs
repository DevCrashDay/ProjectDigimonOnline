using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>Events produced by <see cref="RelayClient"/>, drained on the game thread with TryPoll.</summary>
    public abstract class RelayEvent { }
    public sealed class EvWelcome : RelayEvent { public int ClientId; public string ServerName; }
    public sealed class EvRoomJoined : RelayEvent
    {
        public string Code, Name;
        public int MySlot, HostSlot, MaxPlayers;
        public byte Visibility;
        public bool HasPassword;
    }
    public sealed class EvRoomList : RelayEvent { public int PlayersOnline; public RoomInfo[] Rooms; }
    public sealed class EvPeerJoined : RelayEvent { public int Slot; public string Name; }
    public sealed class EvPeerLeft : RelayEvent { public int Slot; }
    public sealed class EvError : RelayEvent { public string Text; }
    public sealed class EvData : RelayEvent { public int FromSlot; public byte[] Payload; }
    public sealed class EvPong : RelayEvent { public double RttMs; }
    public sealed class EvDisconnected : RelayEvent { public string Reason; }

    /// <summary>
    /// Thread-safe client for <see cref="RelayServer"/>. A background thread reads frames into a queue;
    /// sends happen inline on the caller's thread.
    /// </summary>
    public sealed class RelayClient : IDisposable
    {
        private readonly ConcurrentQueue<RelayEvent> _events = new ConcurrentQueue<RelayEvent>();
        private readonly object _sendLock = new object();
        private TcpClient _tcp;
        private Stream _stream;
        private Thread _reader;
        private volatile bool _open;

        public bool Connected => _open;

        /// <summary>Signalled whenever an event is queued, so a waiting game thread can wake immediately.</summary>
        public readonly AutoResetEvent Arrived = new AutoResetEvent(false);

        /// <summary>Blocking. Raw TCP to host:port.</summary>
        public void Connect(string host, int port, string playerName, int timeoutMs = 5000) =>
            Connect(new ServerAddress { Host = host, Port = port }, playerName, timeoutMs);

        /// <summary>Blocking. Address forms: "wss://lobby.example.com", "ws://host:port/path", "host:port", "host".</summary>
        public void Connect(string address, string playerName, int timeoutMs = 8000) =>
            Connect(ServerAddress.Parse(address), playerName, timeoutMs);

        private void Connect(ServerAddress a, string playerName, int timeoutMs)
        {
            _tcp = new TcpClient();
            var ar = _tcp.BeginConnect(a.Host, a.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { _tcp.Close(); } catch { }
                throw new IOException("Timed out connecting to " + a);
            }
            _tcp.EndConnect(ar);
            Wire.Tune(_tcp);
            try
            {
                _tcp.ReceiveTimeout = timeoutMs; // handshakes only; the reader blocks without a limit
                _tcp.SendTimeout = timeoutMs;
                Stream s = _tcp.GetStream();
                if (a.Tls)
                {
                    var ssl = new SslStream(s, false, (sender, cert, chain, errors) => errors == SslPolicyErrors.None);
                    ssl.AuthenticateAsClient(a.Host, null, SslProtocols.Tls12, false);
                    s = ssl;
                }
                if (a.WebSocket) s = WsStream.Connect(s, a.HostHeader, a.Path);
                _tcp.ReceiveTimeout = 0;
                _tcp.SendTimeout = 0;
                _stream = s;
            }
            catch
            {
                try { _tcp.Close(); } catch { }
                throw;
            }
            _open = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "netplay-reader" };
            _reader.Start();
            Send(Wire.Build(Msg.Hello, w => { w.Write(Msg.ProtocolVersion); w.Write(playerName ?? ""); }));
        }

        public void CreateRoom(int maxPlayers, string name = "", byte visibility = RoomVisibility.Public, string password = "") =>
            Send(Wire.Build(Msg.Create, w => { w.Write((byte)maxPlayers); w.Write(name ?? ""); w.Write(visibility); w.Write(password ?? ""); }));
        public void JoinRoom(string code, string password = "") => Send(Wire.Build(Msg.Join, w => { w.Write(code ?? ""); w.Write(password ?? ""); }));
        public void ListRooms() => Send(Wire.Build(Msg.ListRooms));
        public void SetInMatch(bool inMatch) => Send(Wire.Build(Msg.SetInMatch, w => w.Write(inMatch)));
        public void LeaveRoom() => Send(Wire.Build(Msg.Leave));
        public void Ping() => Send(Wire.Build(Msg.Ping, w => w.Write(DateTime.UtcNow.Ticks)));

        /// <summary>Forward a payload to every other member of the current room.</summary>
        public void Broadcast(byte[] payload) => Send(Wire.Build(Msg.Relay, w => Wire.WriteBytes(w, payload)));

        public bool TryPoll(out RelayEvent ev) => _events.TryDequeue(out ev);

        private void Send(byte[] frame)
        {
            if (!_open) return;
            try
            {
                lock (_sendLock) _stream.Write(frame, 0, frame.Length);
            }
            catch (Exception e)
            {
                Close("send failed: " + e.Message);
            }
        }

        private void ReadLoop()
        {
            string reason = "connection closed";
            try
            {
                while (_open)
                {
                    byte[] frame = Wire.ReadFrame(_stream);
                    if (frame == null) break;
                    var ev = Decode(frame);
                    if (ev != null)
                    {
                        _events.Enqueue(ev);
                        Arrived.Set();
                    }
                }
            }
            catch (Exception e)
            {
                reason = e.Message;
            }
            Close(reason);
        }

        private static RelayEvent Decode(byte[] frame)
        {
            using (var r = Wire.Reader(frame, out byte type))
            {
                switch (type)
                {
                    case Msg.Welcome: return new EvWelcome { ClientId = r.ReadInt32(), ServerName = r.ReadString() };
                    case Msg.RoomJoined:
                        return new EvRoomJoined
                        {
                            Code = r.ReadString(), MySlot = r.ReadByte(), HostSlot = r.ReadByte(), MaxPlayers = r.ReadByte(),
                            Name = r.ReadString(), Visibility = r.ReadByte(), HasPassword = r.ReadBoolean(),
                        };
                    case Msg.RoomList:
                    {
                        var ev = new EvRoomList { PlayersOnline = r.ReadInt32() };
                        int n = r.ReadInt32();
                        if (n < 0 || n > 1000) throw new IOException("bad room list");
                        ev.Rooms = new RoomInfo[n];
                        for (int i = 0; i < n; i++) ev.Rooms[i] = RoomInfo.Read(r);
                        return ev;
                    }
                    case Msg.PeerJoined: return new EvPeerJoined { Slot = r.ReadByte(), Name = r.ReadString() };
                    case Msg.PeerLeft: return new EvPeerLeft { Slot = r.ReadByte() };
                    case Msg.Error: return new EvError { Text = r.ReadString() };
                    case Msg.Relayed: return new EvData { FromSlot = r.ReadByte(), Payload = Wire.ReadBytes(r) };
                    case Msg.Pong: return new EvPong { RttMs = (DateTime.UtcNow.Ticks - r.ReadInt64()) / 10000.0 };
                    default: return null;
                }
            }
        }

        private void Close(string reason)
        {
            if (!_open) return;
            _open = false;
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _events.Enqueue(new EvDisconnected { Reason = reason });
            Arrived.Set();
        }

        public void Dispose() => Close("closed by user");
    }

    /// <summary>Where a relay server is and how to talk to it.</summary>
    public sealed class ServerAddress
    {
        public const int DefaultPort = 7777;

        public string Host, Path = "/";
        public int Port = DefaultPort;
        public bool WebSocket, Tls;

        public string HostHeader => (Tls && Port == 443) || (!Tls && WebSocket && Port == 80) ? Host : Host + ":" + Port;

        public static ServerAddress Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new ArgumentException("No server address set.");
            var a = new ServerAddress();
            string rest = text;
            if (text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) { a.WebSocket = a.Tls = true; a.Port = 443; rest = text.Substring(6); }
            else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { a.WebSocket = a.Tls = true; a.Port = 443; rest = text.Substring(8); }
            else if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) { a.WebSocket = true; a.Port = 80; rest = text.Substring(5); }
            else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { a.WebSocket = true; a.Port = 80; rest = text.Substring(7); }
            int slash = rest.IndexOf('/');
            if (slash >= 0) { a.Path = rest.Substring(slash); rest = rest.Substring(0, slash); }
            int colon = rest.LastIndexOf(':');
            if (colon > 0 && rest.IndexOf(':') == colon)
            {
                if (!int.TryParse(rest.Substring(colon + 1), out int port) || port <= 0 || port > 65535) throw new ArgumentException("Bad port in " + text);
                a.Port = port;
                rest = rest.Substring(0, colon);
            }
            a.Host = rest;
            if (a.Host.Length == 0) throw new ArgumentException("Bad server address " + text);
            return a;
        }

        public override string ToString() => (Tls ? "wss://" : WebSocket ? "ws://" : "") + Host + ":" + Port + (WebSocket ? Path : "");
    }
}
