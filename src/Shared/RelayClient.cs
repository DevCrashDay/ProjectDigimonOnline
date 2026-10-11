using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>Events produced by <see cref="RelayClient"/>, drained on the game thread with TryPoll.</summary>
    public abstract class RelayEvent { }
    public sealed class EvWelcome : RelayEvent { public int ClientId; }
    public sealed class EvRoomJoined : RelayEvent { public string Code; public int MySlot; public int HostSlot; public int MaxPlayers; }
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
        private NetworkStream _stream;
        private Thread _reader;
        private volatile bool _open;

        public bool Connected => _open;

        /// <summary>Signalled whenever an event is queued, so a waiting game thread can wake immediately.</summary>
        public readonly AutoResetEvent Arrived = new AutoResetEvent(false);

        public void Connect(string host, int port, string playerName, int timeoutMs = 5000)
        {
            _tcp = new TcpClient();
            var ar = _tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { _tcp.Close(); } catch { }
                throw new IOException("Timed out connecting to " + host + ":" + port);
            }
            _tcp.EndConnect(ar);
            Wire.Tune(_tcp);
            _stream = _tcp.GetStream();
            _open = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "netplay-reader" };
            _reader.Start();
            Send(Wire.Build(Msg.Hello, w => { w.Write(Msg.ProtocolVersion); w.Write(playerName ?? ""); }));
        }

        public void CreateRoom(int maxPlayers) => Send(Wire.Build(Msg.Create, w => w.Write((byte)maxPlayers)));
        public void JoinRoom(string code) => Send(Wire.Build(Msg.Join, w => w.Write(code ?? "")));
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
                    case Msg.Welcome: return new EvWelcome { ClientId = r.ReadInt32() };
                    case Msg.RoomJoined: return new EvRoomJoined { Code = r.ReadString(), MySlot = r.ReadByte(), HostSlot = r.ReadByte(), MaxPlayers = r.ReadByte() };
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
            try { _tcp?.Close(); } catch { }
            _events.Enqueue(new EvDisconnected { Reason = reason });
            Arrived.Set();
        }

        public void Dispose() => Close("closed by user");
    }
}
