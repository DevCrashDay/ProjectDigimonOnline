using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DigimonNetplay.Shared
{
    /// <summary>
    /// Minimal WebSocket (RFC 6455) carrying the relay's byte stream, so the server can sit behind an HTTPS proxy
    /// such as Cloudflare. Each Write is sent as one binary message; Read returns message payloads back to back,
    /// so the length-prefixed <see cref="Wire"/> framing works on top unchanged. Written by hand because Unity's
    /// Mono runtime has no dependable WebSocket client.
    /// </summary>
    public sealed class WsStream : Stream
    {
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessage = 320 * 1024;

        private readonly Stream _inner;
        private readonly bool _client;   // clients mask what they send
        private readonly object _writeLock = new object();
        private readonly Random _rng = new Random();
        private long _left;              // payload bytes of the current data frame not read yet
        private byte[] _mask;
        private int _maskPos;
        private bool _closed;

        private WsStream(Stream inner, bool client) { _inner = inner; _client = client; }

        // ------------------------------------------------------------------ handshakes

        /// <summary>Client side: upgrade an open connection to host/path.</summary>
        public static WsStream Connect(Stream s, string host, string path)
        {
            var keyBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);
            string req = "GET " + (string.IsNullOrEmpty(path) ? "/" : path) + " HTTP/1.1\r\n" +
                         "Host: " + host + "\r\n" +
                         "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                         "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n" +
                         "User-Agent: DigimonNetplay\r\n\r\n";
            byte[] buf = Encoding.ASCII.GetBytes(req);
            s.Write(buf, 0, buf.Length);
            s.Flush();

            var head = ReadHttpHead(s, null);
            if (head == null) throw new IOException("The server closed the connection during the WebSocket handshake.");
            string[] status = head.StartLine.Split(' ');
            if (status.Length < 2 || status[1] != "101")
                throw new IOException("The server did not accept the WebSocket connection (" + head.StartLine + "). Check the server address.");
            head.Headers.TryGetValue("sec-websocket-accept", out string accept);
            if (accept != AcceptKey(key)) throw new IOException("Bad WebSocket handshake reply from the server.");
            return new WsStream(s, client: true);
        }

        /// <summary>Server side: finish the upgrade for a request whose head was already read.</summary>
        public static WsStream Accept(Stream s, HttpHead head)
        {
            head.Headers.TryGetValue("sec-websocket-key", out string key);
            string resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          "Sec-WebSocket-Accept: " + AcceptKey(key ?? "") + "\r\n\r\n";
            byte[] buf = Encoding.ASCII.GetBytes(resp);
            s.Write(buf, 0, buf.Length);
            s.Flush();
            return new WsStream(s, client: false);
        }

        private static string AcceptKey(string key)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + Guid)));
        }

        public sealed class HttpHead
        {
            public string StartLine;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Method => StartLine.Split(' ')[0];
            public string Path { get { var p = StartLine.Split(' '); return p.Length > 1 ? p[1] : "/"; } }
            public bool IsUpgrade => Headers.TryGetValue("upgrade", out string u) && u.Equals("websocket", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads an HTTP request or response head (start line and headers). <paramref name="already"/> holds bytes read earlier.</summary>
        public static HttpHead ReadHttpHead(Stream s, byte[] already)
        {
            var bytes = new List<byte>(512);
            if (already != null) bytes.AddRange(already);
            var one = new byte[1];
            while (!EndsWithBlankLine(bytes))
            {
                if (bytes.Count > 16 * 1024) throw new IOException("HTTP head too large");
                int n = s.Read(one, 0, 1);
                if (n <= 0) return null;
                bytes.Add(one[0]);
            }
            string[] lines = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var head = new HttpHead { StartLine = lines[0] };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) head.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return head;
        }

        private static bool EndsWithBlankLine(List<byte> b)
        {
            int n = b.Count;
            return n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n';
        }

        // ------------------------------------------------------------------ frames

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_left == 0)
            {
                if (_closed || !NextDataFrame()) return 0;
            }
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
            if (n <= 0) { _closed = true; return 0; }
            if (_mask != null)
                for (int i = 0; i < n; i++) buffer[offset + i] ^= _mask[_maskPos++ & 3];
            _left -= n;
            return n;
        }

        /// <summary>Reads frame headers until a data frame with payload starts. Answers pings, stops on close.</summary>
        private bool NextDataFrame()
        {
            var h = new byte[2];
            if (!ReadExact(h, 2)) return false;
            int opcode = h[0] & 0x0F;
            bool masked = (h[1] & 0x80) != 0;
            long len = h[1] & 0x7F;
            if (len == 126)
            {
                var e = new byte[2];
                if (!ReadExact(e, 2)) return false;
                len = (e[0] << 8) | e[1];
            }
            else if (len == 127)
            {
                var e = new byte[8];
                if (!ReadExact(e, 8)) return false;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
            }
            if (len < 0 || len > MaxMessage) throw new IOException("WebSocket frame too large");
            byte[] mask = null;
            if (masked)
            {
                mask = new byte[4];
                if (!ReadExact(mask, 4)) return false;
            }

            if (opcode >= 0x8)
            {
                // control frame: read it whole
                var payload = new byte[len];
                if (!ReadExact(payload, (int)len)) return false;
                if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                if (opcode == 0x8)
                {
                    _closed = true;
                    try { SendFrame(0x8, payload, 0, Math.Min(payload.Length, 2)); } catch { }
                    return false;
                }
                if (opcode == 0x9) SendFrame(0xA, payload, 0, payload.Length);
                return true; // pong or ping handled; keep looking
            }

            _left = len;
            _mask = mask;
            _maskPos = 0;
            return true;
        }

        private bool ReadExact(byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _inner.Read(buf, got, count - got);
                if (n <= 0) { _closed = true; return false; }
                got += n;
            }
            return true;
        }

        public override void Write(byte[] buffer, int offset, int count) => SendFrame(0x2, buffer, offset, count);

        private void SendFrame(int opcode, byte[] data, int offset, int count)
        {
            int headLen = 2 + (count >= 126 ? (count > 0xFFFF ? 8 : 2) : 0) + (_client ? 4 : 0);
            var frame = new byte[headLen + count];
            frame[0] = (byte)(0x80 | opcode);
            int p = 2;
            if (count < 126) frame[1] = (byte)count;
            else if (count <= 0xFFFF) { frame[1] = 126; frame[2] = (byte)(count >> 8); frame[3] = (byte)count; p = 4; }
            else
            {
                frame[1] = 127;
                for (int i = 0; i < 8; i++) frame[2 + i] = (byte)((long)count >> (56 - 8 * i));
                p = 10;
            }
            if (_client)
            {
                frame[1] |= 0x80;
                var mask = new byte[4];
                lock (_rng) _rng.NextBytes(mask);
                Buffer.BlockCopy(mask, 0, frame, p, 4);
                p += 4;
                for (int i = 0; i < count; i++) frame[p + i] = (byte)(data[offset + i] ^ mask[i & 3]);
            }
            else Buffer.BlockCopy(data, offset, frame, p, count);
            lock (_writeLock)
            {
                _inner.Write(frame, 0, frame.Length);
                _inner.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_closed)
                {
                    _closed = true;
                    try { SendFrame(0x8, new byte[] { 0x03, 0xE8 }, 0, 2); } catch { }
                }
                try { _inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A stream that first returns some bytes already read, then continues with the inner stream.</summary>
    public sealed class PrefixedStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _pos;

        public PrefixedStream(byte[] prefix, Stream inner) { _prefix = prefix; _inner = inner; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < _prefix.Length)
            {
                int n = Math.Min(count, _prefix.Length - _pos);
                Buffer.BlockCopy(_prefix, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() => _inner.Flush();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
