using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Optional direct (peer-to-peer) UDP links between the players of a room, with the relay server as fallback.
    ///
    /// Each player finds its public UDP address with a STUN server and announces it, with its LAN addresses, through
    /// the relay. Players then "punch" towards each other. During a match every lockstep message gets a sequence
    /// number and goes straight to every peer whose link is up, and through the relay to the rest. UDP can lose
    /// packets, so unacknowledged messages are resent; receivers drop duplicates. If a link goes quiet mid-match,
    /// everything that peer has not confirmed is resent through the relay and the match carries on there.
    /// Lockstep messages are keyed by frame, so arrival order does not matter.
    /// </summary>
    public sealed class P2PMux : IDisposable
    {
        private const uint Magic = 0x31504E44; // "DNP1"
        private const byte TProbe = 1, TProbeAck = 2, TData = 3, TAck = 4;
        private const int MaxPacket = 1100;
        private const int ProbeIntervalMs = 200, KeepAliveMs = 250, LinkTimeoutMs = 1500, ResendAfterMs = 50;
        /// <summary>A message a live link has not delivered by then also goes through the relay, so a bad patch never stalls long.</summary>
        private const int RelayBackupMs = 400;

        private sealed class Peer
        {
            public int Slot;
            public byte[] Token;
            public List<IPEndPoint> Candidates = new List<IPEndPoint>();
            public IPEndPoint Ep;
            public bool Up;
            public long LastHeard, LastProbe;
            public bool Cut; // tests: simulate a dead link
        }

        private sealed class Pending
        {
            public int Seq;
            public byte[] Payload;
            public long LastSent, Created;
            public readonly HashSet<int> Done = new HashSet<int>(); // peers that have it (acked, or sent via relay)
        }

        private sealed class RecvState
        {
            public int MatchId;
            public readonly HashSet<int> Seen = new HashSet<int>();
            public readonly HashSet<int> OldMatches = new HashSet<int>();
        }

        public readonly int MySlot;
        public readonly bool UdpEnabled;
        public string[] StunServers = { "stun.l.google.com:19302", "stun.cloudflare.com:3478" };
        public Action<string> Log = _ => { };
        /// <summary>Tests only: fraction of outgoing UDP packets to drop.</summary>
        public double DropRate;

        private readonly Action<byte[]> _relayBroadcast;
        private readonly AutoResetEvent _signal;
        private readonly object _lock = new object();
        private readonly byte[] _token = new byte[8];
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Dictionary<int, RecvState> _recv = new Dictionary<int, RecvState>();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly ConcurrentQueue<KeyValuePair<int, byte[]>> _inbox = new ConcurrentQueue<KeyValuePair<int, byte[]>>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _rng = new Random();
        private int[] _members = new int[0];
        private int _matchId, _nextSeq;
        private Socket _udp;
        private volatile bool _running;
        private IPEndPoint _public;
        private byte[] _stunTx;

        public P2PMux(int mySlot, bool enableUdp, Action<byte[]> relayBroadcast, AutoResetEvent signal)
        {
            MySlot = mySlot;
            _relayBroadcast = relayBroadcast;
            _signal = signal;
            _rng.NextBytes(_token);
            if (!enableUdp) return;
            try
            {
                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // Without this, Windows reports an ICMP "port unreachable" from an old probe as an error on the next receive.
                    try { _udp.IOControl(-1744830452, new byte[4], null); } catch { }
                }
                _udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                UdpEnabled = true;
                _running = true;
                new Thread(ReceiveLoop) { IsBackground = true, Name = "p2p-recv" }.Start();
                new Thread(TickLoop) { IsBackground = true, Name = "p2p-tick" }.Start();
            }
            catch (Exception e)
            {
                Log("P2P disabled, could not open a UDP port: " + e.Message);
                try { _udp?.Close(); } catch { }
                _udp = null;
                UdpEnabled = false;
            }
        }

        private long Now => _clock.ElapsedMilliseconds;
        public int LocalPort => _udp == null ? 0 : ((IPEndPoint)_udp.LocalEndPoint).Port;
        public IPEndPoint PublicEndPoint => _public;

        // ------------------------------------------------------------------ discovery

        /// <summary>Asks the STUN servers for our public address (in the background), then announces us to the room.</summary>
        public void StartDiscovery()
        {
            if (!UdpEnabled) return;
            Announce(); // LAN addresses right away; the public one follows
            new Thread(() =>
            {
                foreach (string server in StunServers)
                {
                    if (!_running || _public != null) break;
                    try
                    {
                        var parts = server.Split(':');
                        var ip = Dns.GetHostAddresses(parts[0]).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (ip == null) continue;
                        var tx = new byte[12];
                        lock (_rng) _rng.NextBytes(tx);
                        _stunTx = tx;
                        var req = new byte[20];
                        req[1] = 0x01; // binding request, length 0
                        req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
                        Buffer.BlockCopy(tx, 0, req, 8, 12);
                        var to = new IPEndPoint(ip, int.Parse(parts[1]));
                        for (int attempt = 0; attempt < 3 && _public == null; attempt++)
                        {
                            _udp.SendTo(req, to);
                            for (int w = 0; w < 10 && _public == null; w++) Thread.Sleep(50);
                        }
                    }
                    catch (Exception e) { Log("STUN " + server + ": " + e.Message); }
                }
                if (_public != null) { Log("P2P public address " + _public); Announce(); }
                else Log("P2P: no public address found (LAN only)");
            }) { IsBackground = true, Name = "p2p-stun" }.Start();
        }

        /// <summary>Tells the room how to reach us (or, with UDP off, nothing: peers then always use the relay for us).</summary>
        public void Announce()
        {
            if (!UdpEnabled) return;
            var cands = Candidates();
            _relayBroadcast(GameMsg.Build(GameMsg.P2PHello, w =>
            {
                w.Write(_token);
                w.Write((byte)cands.Count);
                foreach (var c in cands) w.Write(c.ToString());
            }));
        }

        private List<IPEndPoint> Candidates()
        {
            var list = new List<IPEndPoint>();
            int port = LocalPort;
            if (_public != null) list.Add(_public);
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            list.Add(new IPEndPoint(ua.Address, port));
                }
            }
            catch { }
            list.Add(new IPEndPoint(IPAddress.Loopback, port)); // two games on one PC
            return list.GroupBy(e => e.ToString()).Select(g => g.First()).Take(8).ToList();
        }

        /// <summary>Handles a peer's GameMsg.P2PHello (already past the type byte).</summary>
        public void OnHello(int slot, BinaryReader r)
        {
            byte[] token = r.ReadBytes(8);
            int n = r.ReadByte();
            var cands = new List<IPEndPoint>();
            for (int i = 0; i < n; i++)
            {
                string s = r.ReadString();
                int colon = s.LastIndexOf(':');
                if (colon > 0 && IPAddress.TryParse(s.Substring(0, colon), out var ip) && int.TryParse(s.Substring(colon + 1), out int port))
                    cands.Add(new IPEndPoint(ip, port));
            }
            if (slot == MySlot || !UdpEnabled) return;
            lock (_lock)
            {
                if (!_peers.TryGetValue(slot, out var p) || !p.Token.SequenceEqual(token))
                    _peers[slot] = p = new Peer { Slot = slot, Token = token };
                p.Candidates = cands;
                p.LastProbe = 0;
            }
        }

        public void RemovePeer(int slot)
        {
            lock (_lock) _peers.Remove(slot);
        }

        public bool IsDirect(int slot)
        {
            lock (_lock) return _peers.TryGetValue(slot, out var p) && p.Up;
        }

        /// <summary>Tests only: make a link look dead (or alive again).</summary>
        public void CutLink(int slot, bool cut)
        {
            lock (_lock) if (_peers.TryGetValue(slot, out var p)) p.Cut = cut;
        }

        // ------------------------------------------------------------------ match messages

        /// <summary>Starts a match's message stream. <paramref name="otherSlots"/> are everyone who must receive it.</summary>
        public void BeginMatch(int matchId, IEnumerable<int> otherSlots)
        {
            lock (_lock)
            {
                _matchId = matchId;
                _nextSeq = 0;
                _pending.Clear();
                _members = otherSlots.Where(s => s != MySlot).ToArray();
            }
        }

        /// <summary>Lockstep broadcast: direct to peers with a live link, through the relay to the rest.</summary>
        public void Send(byte[] payload)
        {
            Pending p;
            bool needRelay = false;
            var direct = new List<Peer>();
            lock (_lock)
            {
                p = new Pending { Seq = _nextSeq++, Payload = payload, LastSent = Now, Created = Now };
                foreach (int slot in _members)
                {
                    if (_peers.TryGetValue(slot, out var peer) && peer.Up) direct.Add(peer);
                    else { p.Done.Add(slot); needRelay = true; }
                }
                if (direct.Count > 0) _pending.Add(p);
            }
            if (needRelay) _relayBroadcast(Wrap(p));
            foreach (var peer in direct) SendData(peer);
        }

        private byte[] Wrap(Pending p) => GameMsg.Build(GameMsg.Seq, w => { w.Write(_matchId); w.Write(p.Seq); Wire.WriteBytes(w, p.Payload); });

        /// <summary>Handles a GameMsg.Seq that came through the relay (already past the type byte).</summary>
        public void OnRelayed(int from, BinaryReader r)
        {
            int matchId = r.ReadInt32(), seq = r.ReadInt32();
            Deliver(from, matchId, seq, Wire.ReadBytes(r));
        }

        public bool TryReceive(out int from, out byte[] payload)
        {
            if (_inbox.TryDequeue(out var kv)) { from = kv.Key; payload = kv.Value; return true; }
            from = -1; payload = null;
            return false;
        }

        private bool Deliver(int from, int matchId, int seq, byte[] payload)
        {
            lock (_lock)
            {
                if (!_recv.TryGetValue(from, out var rs)) _recv[from] = rs = new RecvState { MatchId = matchId };
                if (rs.MatchId != matchId)
                {
                    if (rs.OldMatches.Contains(matchId)) return false; // straggler from an earlier match
                    rs.OldMatches.Add(rs.MatchId);
                    rs.MatchId = matchId;
                    rs.Seen.Clear();
                }
                if (!rs.Seen.Add(seq)) return false;
            }
            _inbox.Enqueue(new KeyValuePair<int, byte[]>(from, payload));
            _signal?.Set();
            return true;
        }

        // ------------------------------------------------------------------ UDP

        private byte[] Packet(byte type, Action<BinaryWriter> body = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic); w.Write(type); w.Write((byte)MySlot); w.Write(_token);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        private void SendPacket(byte[] packet, IPEndPoint to, Peer peer)
        {
            if (_udp == null || to == null || (peer != null && peer.Cut)) return;
            if (DropRate > 0) lock (_rng) if (_rng.NextDouble() < DropRate) return;
            try { _udp.SendTo(packet, to); } catch { }
        }

        /// <summary>Sends every message this peer has not confirmed yet, as many as fit in one packet.</summary>
        private void SendData(Peer peer)
        {
            var batch = new List<Pending>();
            int size = 20;
            int matchId;
            lock (_lock)
            {
                matchId = _matchId;
                foreach (var p in _pending)
                {
                    if (p.Done.Contains(peer.Slot)) continue;
                    if (size + 6 + p.Payload.Length > MaxPacket) break;
                    size += 6 + p.Payload.Length;
                    batch.Add(p);
                    p.LastSent = Now;
                }
            }
            if (batch.Count == 0) return;
            SendPacket(Packet(TData, w =>
            {
                w.Write(matchId);
                w.Write((ushort)batch.Count);
                foreach (var p in batch) { w.Write(p.Seq); w.Write((ushort)p.Payload.Length); w.Write(p.Payload); }
            }), peer.Ep, peer);
        }

        private void ReceiveLoop()
        {
            var buf = new byte[2048];
            while (_running)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = _udp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                catch { return; }
                try { OnPacket(buf, n, (IPEndPoint)from); }
                catch (Exception e) { Log("P2P bad packet: " + e.Message); }
            }
        }

        private void OnPacket(byte[] buf, int n, IPEndPoint from)
        {
            if (n >= 20 && buf[0] == 0x01 && buf[1] == 0x01 && buf[4] == 0x21 && buf[5] == 0x12 && buf[6] == 0xA4 && buf[7] == 0x42)
            {
                OnStun(buf, n);
                return;
            }
            if (n < 14) return;
            using (var r = new BinaryReader(new MemoryStream(buf, 0, n)))
            {
                if (r.ReadUInt32() != Magic) return;
                byte type = r.ReadByte();
                int slot = r.ReadByte();
                byte[] token = r.ReadBytes(8);
                Peer peer;
                lock (_lock)
                {
                    if (!_peers.TryGetValue(slot, out peer) || !peer.Token.SequenceEqual(token) || peer.Cut) return;
                    peer.Ep = from; // also follows the peer's NAT if its port changes
                    peer.LastHeard = Now;
                    if ((type == TProbeAck || type == TData) && !peer.Up)
                    {
                        peer.Up = true;
                        Log("P2P direct link to slot " + slot + " via " + from);
                    }
                }
                switch (type)
                {
                    case TProbe:
                        SendPacket(Packet(TProbeAck), from, peer);
                        break;
                    case TData:
                    {
                        int matchId = r.ReadInt32();
                        int count = r.ReadUInt16();
                        var seqs = new List<int>(count);
                        for (int i = 0; i < count; i++)
                        {
                            int seq = r.ReadInt32();
                            int len = r.ReadUInt16();
                            byte[] payload = r.ReadBytes(len);
                            seqs.Add(seq);
                            Deliver(slot, matchId, seq, payload);
                        }
                        SendPacket(Packet(TAck, w => { w.Write(matchId); w.Write((ushort)seqs.Count); foreach (int s in seqs) w.Write(s); }), from, peer);
                        break;
                    }
                    case TAck:
                    {
                        int matchId = r.ReadInt32();
                        int count = r.ReadUInt16();
                        var seqs = new HashSet<int>();
                        for (int i = 0; i < count; i++) seqs.Add(r.ReadInt32());
                        lock (_lock)
                        {
                            if (matchId != _matchId) break;
                            foreach (var p in _pending) if (seqs.Contains(p.Seq)) p.Done.Add(slot);
                            _pending.RemoveAll(p => _members.All(m => p.Done.Contains(m)));
                        }
                        break;
                    }
                }
            }
        }

        private void OnStun(byte[] buf, int n)
        {
            var tx = _stunTx;
            if (tx == null) return;
            for (int i = 0; i < 12; i++) if (buf[8 + i] != tx[i]) return;
            int pos = 20;
            while (pos + 4 <= n)
            {
                int type = (buf[pos] << 8) | buf[pos + 1];
                int len = (buf[pos + 2] << 8) | buf[pos + 3];
                int v = pos + 4;
                if (v + len > n) return;
                if ((type == 0x0020 || type == 0x0001) && len >= 8 && buf[v + 1] == 0x01)
                {
                    int port = (buf[v + 2] << 8) | buf[v + 3];
                    var ip = new byte[4];
                    Buffer.BlockCopy(buf, v + 4, ip, 0, 4);
                    if (type == 0x0020)
                    {
                        port ^= 0x2112;
                        ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42;
                    }
                    _public = new IPEndPoint(new IPAddress(ip), port);
                    return;
                }
                pos = v + ((len + 3) & ~3);
            }
        }

        private void TickLoop()
        {
            while (_running)
            {
                Thread.Sleep(20);
                long now = Now;
                var probes = new List<KeyValuePair<Peer, IPEndPoint>>();
                var resend = new List<Peer>();
                var fallback = new List<Pending>();
                lock (_lock)
                {
                    foreach (var peer in _peers.Values)
                    {
                        if (peer.Up && now - peer.LastHeard > LinkTimeoutMs)
                        {
                            peer.Up = false;
                            peer.LastProbe = 0;
                            Log("P2P link to slot " + peer.Slot + " lost; using the server for it");
                            foreach (var p in _pending)
                                if (_members.Contains(peer.Slot) && p.Done.Add(peer.Slot)) fallback.Add(p);
                        }
                        if (now - peer.LastProbe >= (peer.Up ? KeepAliveMs : ProbeIntervalMs))
                        {
                            peer.LastProbe = now;
                            if (peer.Up) probes.Add(new KeyValuePair<Peer, IPEndPoint>(peer, peer.Ep));
                            else
                            {
                                if (peer.Ep != null) probes.Add(new KeyValuePair<Peer, IPEndPoint>(peer, peer.Ep));
                                foreach (var c in peer.Candidates) probes.Add(new KeyValuePair<Peer, IPEndPoint>(peer, c));
                            }
                        }
                        if (peer.Up)
                        {
                            foreach (var p in _pending)
                                if (now - p.Created >= RelayBackupMs && !p.Done.Contains(peer.Slot) && !fallback.Contains(p))
                                {
                                    p.Done.Add(peer.Slot);
                                    fallback.Add(p);
                                }
                            if (_pending.Any(p => !p.Done.Contains(peer.Slot) && now - p.LastSent >= ResendAfterMs)) resend.Add(peer);
                        }
                    }
                    _pending.RemoveAll(p => _members.All(m => p.Done.Contains(m)));
                }
                foreach (var p in fallback) _relayBroadcast(Wrap(p));
                if (probes.Count > 0)
                {
                    byte[] probe = Packet(TProbe);
                    foreach (var kv in probes) SendPacket(probe, kv.Value, kv.Key);
                }
                foreach (var peer in resend) SendData(peer);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _udp?.Close(); } catch { }
        }
    }
}
