using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace IGTAPTasMod
{
    internal sealed class WebStudioRequest
    {
        public string Method;
        public string Path;
        public string Body;
        public string Response;
        public int StatusCode = 200;
        public bool AsyncHandling = false;
        public readonly ManualResetEvent Completed = new ManualResetEvent(false);
        /// <summary>The studio socket the request came in on (its reply goes back there, tagged <see cref="SocketId"/>), null for HTTP.</summary>
        public WebStudioSocket Socket;
        public long SocketId;
    }

    internal sealed class WebStudioServer : IDisposable
    {
        public const int Port = 17426;

        public static string Url => "http://127.0.0.1:" + Port + "/";

        // A studio socket's state checks: at most this often, and at most a tenth of the main thread (the last check's cost × 10).
        const double PushIntervalMs = 33;
        const double PushShare = 10;

        private readonly ConcurrentQueue<WebStudioRequest> _requests = new ConcurrentQueue<WebStudioRequest>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private string _staticRoot;

        // The studio's push (GET /api/ws): the live and static state builders (main thread only) and the sockets they go to.
        private readonly Func<object> _live;
        private readonly Func<long> _staticVersion;
        private readonly Func<object> _static;
        private readonly object _socketsGate = new object();
        private readonly List<WebStudioSocket> _sockets = new List<WebStudioSocket>();
        private readonly List<WebStudioSocket> _joining = new List<WebStudioSocket>();
        // The sockets a check pushes to, copied under the lock into this one list (main thread only) instead of a new array.
        private readonly List<WebStudioSocket> _pushTargets = new List<WebStudioSocket>();
        private readonly Stopwatch _pushClock = Stopwatch.StartNew();
        private double _pushDue;
        private double _pushWarnedAt = double.NegativeInfinity;
        // The last state pushed: a socket that joins gets these, and a live state is pushed only when its JSON differs.
        private string _liveJson;
        private byte[] _liveFrame;
        private byte[] _staticFrame;
        private long _staticFrameVersion;

        public WebStudioServer(Func<object> live, Func<long> staticVersion, Func<object> staticState)
        {
            _live = live;
            _staticVersion = staticVersion;
            _static = staticState;
        }

        // A TcpListener on 127.0.0.1, not HttpListener: Mono's HttpListener needs URL-ACL reservations.
        public void Start(string staticRoot)
        {
            if (_running) return;
            _staticRoot = Path.GetFullPath(staticRoot);
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "IGTAP TAS Web Studio" };
            _thread.Start();
        }

        /// <summary>
        /// Runs the queued requests on the calling (main) thread. A GET without a body is answered once per pump: the same
        /// path queued again in this pump (several tabs, tools) gets the first one's response, built and serialised once. Any
        /// other request can change what a GET returns, so it drops the answers kept so far. Then the studio sockets get the
        /// state that changed (<see cref="PushState"/>), and after it the replies to their requests answered in this pump, so a
        /// studio that has a command's reply has the state it left too.
        /// </summary>
        public void Pump(Func<WebStudioRequest, object> handler)
        {
            WebStudioRequest request;
            Dictionary<string, WebStudioRequest> answered = null;
            List<WebStudioRequest> replies = null;
            bool commanded = false;
            while (_requests.TryDequeue(out request))
            {
                bool reusable = request.Method == "GET" && string.IsNullOrEmpty(request.Body);
                if (request.Method != "GET") commanded = true;
                if (!reusable) answered?.Clear();
                else if (answered != null && answered.TryGetValue(request.Path, out WebStudioRequest first))
                {
                    request.StatusCode = first.StatusCode;
                    request.Response = first.Response;
                    Answered(request, ref replies);
                    continue;
                }
                try
                {
                    object result = handler(request);
                    if (request.AsyncHandling) continue;
                    request.Response = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                }
                catch (Exception ex)
                {
                    request.StatusCode = 500;
                    request.Response = Newtonsoft.Json.JsonConvert.SerializeObject(new { error = ex.Message });
                }
                finally
                {
                    if (!request.AsyncHandling)
                    {
                        if (reusable) (answered ?? (answered = new Dictionary<string, WebStudioRequest>(StringComparer.Ordinal)))[request.Path] = request;
                        Answered(request, ref replies);
                    }
                    else if (request.Socket != null) ReplyWhenCompleted(request);
                }
            }
            PushState(commanded);
            if (replies != null)
                foreach (WebStudioRequest reply in replies)
                {
                    reply.Socket.Reply(ReplyFrame(reply));
                    reply.Completed.Dispose();
                }
        }

        private static void Answered(WebStudioRequest request, ref List<WebStudioRequest> replies)
        {
            if (request.Socket == null) request.Completed.Set();
            else (replies ?? (replies = new List<WebStudioRequest>())).Add(request);
        }

        /// <summary>A socket request the handler answers later (AsyncHandling): its reply goes out when it's set, off the main thread.</summary>
        private static void ReplyWhenCompleted(WebStudioRequest request)
        {
            ThreadPool.RegisterWaitForSingleObject(request.Completed, (state, timedOut) =>
            {
                if (timedOut)
                {
                    request.Socket.Reply(ReplyFrame(request.SocketId, 503, "{\"error\":\"Game main thread did not respond within timeout\"}"));
                    return;
                }
                request.Socket.Reply(ReplyFrame(request));
            }, null, 600000, true);
        }

        /// <summary>
        /// Sends the studio sockets the state that changed: the static part when <c>WebStateVersion</c> moved, the live part
        /// when its JSON differs from the last one pushed, each built and serialised once for every socket. A socket that just
        /// joined gets both. Checked at most every <see cref="PushIntervalMs"/> (a command's pump checks at once), and only
        /// while a socket is open.
        /// </summary>
        private void PushState(bool force)
        {
            List<WebStudioSocket> joined = null;
            double now;
            lock (_socketsGate)
            {
                if (_joining.Count > 0)
                {
                    joined = new List<WebStudioSocket>(_joining);
                    _sockets.AddRange(_joining);
                    _joining.Clear();
                }
                if (_sockets.Count == 0) return;
                // Checked before the sockets are copied: between checks (every frame at 280 fps) the pump takes the lock and
                // nothing else.
                now = _pushClock.Elapsed.TotalMilliseconds;
                if (!force && joined == null && now < _pushDue) return;
                _pushTargets.Clear();
                _pushTargets.AddRange(_sockets);
            }
            List<WebStudioSocket> sockets = _pushTargets;
            try
            {
                long version = _staticVersion();
                if (_staticFrame == null || version != _staticFrameVersion)
                {
                    _staticFrame = MessageFrame("{\"type\":\"static\",\"data\":", Newtonsoft.Json.JsonConvert.SerializeObject(_static()), "}");
                    _staticFrameVersion = version;
                    foreach (WebStudioSocket socket in sockets) socket.PushStatic(_staticFrame);
                }
                else if (joined != null) foreach (WebStudioSocket socket in joined) socket.PushStatic(_staticFrame);

                double liveStart = _pushClock.Elapsed.TotalMilliseconds;
                string live = Newtonsoft.Json.JsonConvert.SerializeObject(_live());
                double liveMs = _pushClock.Elapsed.TotalMilliseconds - liveStart;
                if (!string.Equals(live, _liveJson, StringComparison.Ordinal))
                {
                    _liveJson = live;
                    _liveFrame = MessageFrame("{\"type\":\"live\",\"data\":", live, "}");
                    foreach (WebStudioSocket socket in sockets) socket.PushLive(_liveFrame);
                }
                else if (joined != null) foreach (WebStudioSocket socket in joined) socket.PushLive(_liveFrame);

                double end = _pushClock.Elapsed.TotalMilliseconds;
                double interval = Math.Max(PushIntervalMs, (end - now) * PushShare);
                _pushDue = end + interval;
                if (liveMs > 3 && end - _pushWarnedAt > 30000)
                {
                    _pushWarnedAt = end;
                    TasPlugin.Log?.LogWarning("Web Studio: the live state took " + liveMs.ToString("0.0") + " ms to build; its checks run every "
                        + interval.ToString("0") + " ms.");
                }
            }
            catch (Exception ex)
            {
                // Every socket gets both parts again at the next check that builds them (one that joined now got neither).
                _staticFrame = null;
                _liveJson = null;
                _pushDue = now + 1000;
                if (now - _pushWarnedAt > 30000)
                {
                    _pushWarnedAt = now;
                    TasPlugin.Log?.LogWarning("Web Studio: could not build the state to push: " + ex);
                }
            }
        }

        internal void Enqueue(WebStudioRequest request) { _requests.Enqueue(request); }

        internal void Leave(WebStudioSocket socket)
        {
            lock (_socketsGate)
            {
                _sockets.Remove(socket);
                _joining.Remove(socket);
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) { if (_running) Thread.Sleep(100); }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            bool kept = false;
            try
            {
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;
                string header = ReadHeader(stream, out byte[] early, out int earlyCount);
                if (string.IsNullOrEmpty(header)) return;
                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) { WriteResponse(stream, 400, "application/json", "{\"error\":\"Bad request\"}"); return; }
                string method = first[0].ToUpperInvariant();
                string path = first[1].Split('?')[0];
                if (method == "GET" && path == "/api/ws")
                {
                    kept = AcceptSocket(client, stream, lines, early, earlyCount);
                    return;
                }
                int.TryParse(HeaderValue(lines, "Content-Length") ?? "0", out int contentLength);
                string body = ReadBody(stream, contentLength, early, earlyCount);

                if (method == "OPTIONS") { WriteResponse(stream, 204, "text/plain", ""); return; }
                if (method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    ServeStatic(stream, path);
                    return;
                }
                if (!path.StartsWith("/api/", StringComparison.Ordinal))
                { WriteResponse(stream, 404, "application/json", "{\"error\":\"Not found\"}"); return; }

                WebStudioRequest request = new WebStudioRequest { Method = method, Path = path, Body = body };
                _requests.Enqueue(request);
                // A request the main thread never answered may still be set later (a Full-TAS run's pending start), so only an
                // answered one's event is disposed here.
                if (!request.Completed.WaitOne(600000))
                { WriteResponse(stream, 503, "application/json", "{\"error\":\"Game main thread did not respond within timeout\"}"); return; }
                request.Completed.Dispose();
                WriteResponse(stream, request.StatusCode, "application/json; charset=utf-8", request.Response ?? "{}");
            }
            finally
            {
                if (!kept) client.Close();
            }
        }

        /// <summary>
        /// GET /api/ws: the studio's socket (RFC 6455). The next pump sends it the static and live state; see
        /// <see cref="WebStudioSocket"/> for what it sends and takes.
        /// </summary>
        private bool AcceptSocket(TcpClient client, NetworkStream stream, string[] lines, byte[] early, int earlyCount)
        {
            string key = HeaderValue(lines, "Sec-WebSocket-Key");
            string upgrade = HeaderValue(lines, "Upgrade");
            if (key == null || upgrade == null || upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
            {
                WriteResponse(stream, 400, "application/json", "{\"error\":\"Expected a WebSocket upgrade\"}");
                return false;
            }
            string accept;
            using (SHA1 sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
                + accept + "\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.ReadTimeout = Timeout.Infinite;
            client.NoDelay = true;
            var socket = new WebStudioSocket(this, client, stream, early, earlyCount);
            lock (_socketsGate) _joining.Add(socket);
            socket.Start();
            return true;
        }

        private static string HeaderValue(string[] lines, string name)
        {
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > name.Length && line[name.Length] == ':'
                    && string.Compare(line, 0, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return line.Substring(name.Length + 1).Trim();
            }
            return null;
        }

        private void ServeStatic(NetworkStream stream, string requestPath)
        {
            string relative = Uri.UnescapeDataString(requestPath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative)) relative = "index.html";
            string full = Path.GetFullPath(Path.Combine(_staticRoot, relative));
            if (!full.StartsWith(_staticRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { WriteResponse(stream, 404, "text/plain; charset=utf-8", "Web Studio asset not found. Rebuild the plugin."); return; }
            byte[] content = File.ReadAllBytes(full);
            WriteResponse(stream, 200, ContentType(full), content);
        }

        /// <summary>
        /// Reads up to the blank line that ends the header, in chunks. What a chunk holds past it (the body's start) comes back
        /// in <paramref name="early"/>'s first <paramref name="earlyCount"/> bytes for <see cref="ReadBody"/>.
        /// </summary>
        private static string ReadHeader(NetworkStream stream, out byte[] early, out int earlyCount)
        {
            byte[] buffer = new byte[4096];
            int length = 0, scanned = 0;
            early = null;
            earlyCount = 0;
            while (length < 32768)
            {
                if (length == buffer.Length) Array.Resize(ref buffer, Math.Min(buffer.Length * 2, 32768));
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read <= 0) break;
                length += read;
                for (int i = Math.Max(3, scanned); i < length; i++)
                {
                    if (buffer[i] != '\n' || buffer[i - 1] != '\r' || buffer[i - 2] != '\n' || buffer[i - 3] != '\r') continue;
                    earlyCount = length - (i + 1);
                    if (earlyCount > 0)
                    {
                        early = new byte[earlyCount];
                        Buffer.BlockCopy(buffer, i + 1, early, 0, earlyCount);
                    }
                    return Encoding.UTF8.GetString(buffer, 0, i + 1);
                }
                scanned = length;
            }
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        private static string ReadBody(NetworkStream stream, int length, byte[] early, int earlyCount)
        {
            if (length <= 0) return "";
            byte[] bytes = new byte[length];
            int offset = Math.Min(earlyCount, length);
            if (offset > 0) Buffer.BlockCopy(early, 0, bytes, 0, offset);
            while (offset < length)
            {
                int read = stream.Read(bytes, offset, length - offset);
                if (read <= 0) break;
                offset += read;
            }
            return Encoding.UTF8.GetString(bytes, 0, offset);
        }

        private static void WriteResponse(NetworkStream stream, int status, string contentType, string body)
        {
            WriteResponse(stream, status, contentType, Encoding.UTF8.GetBytes(body ?? ""));
        }

        private static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] content)
        {
            string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : "Error";
            string header = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType
                + "\r\nContent-Length: " + content.Length + "\r\nCache-Control: no-store"
                + "\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: Content-Type"
                + "\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nConnection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            if (content.Length > 0) stream.Write(content, 0, content.Length);
        }

        private static string ContentType(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".html") return "text/html; charset=utf-8";
            if (extension == ".js") return "text/javascript; charset=utf-8";
            if (extension == ".css") return "text/css; charset=utf-8";
            if (extension == ".json") return "application/json; charset=utf-8";
            if (extension == ".svg") return "image/svg+xml";
            if (extension == ".png") return "image/png";
            if (extension == ".webp") return "image/webp";
            if (extension == ".ico") return "image/x-icon";
            if (extension == ".woff2") return "font/woff2";
            return "application/octet-stream";
        }

        private static byte[] ReplyFrame(WebStudioRequest request)
        {
            return ReplyFrame(request.SocketId, request.StatusCode, request.Response ?? "{}");
        }

        internal static byte[] ReplyFrame(long id, int status, string json)
        {
            return MessageFrame("{\"type\":\"reply\",\"id\":" + id + ",\"status\":" + status + ",\"data\":", json, "}");
        }

        /// <summary>One unmasked, unfragmented text frame holding <paramref name="prefix"/> + <paramref name="json"/> + <paramref name="suffix"/> (ASCII around UTF-8), encoded straight into it.</summary>
        internal static byte[] MessageFrame(string prefix, string json, string suffix)
        {
            long length = prefix.Length + Encoding.UTF8.GetByteCount(json) + suffix.Length;
            int head = length < 126 ? 2 : length <= ushort.MaxValue ? 4 : 10;
            byte[] frame = new byte[head + length];
            frame[0] = 0x81;
            if (head == 2) frame[1] = (byte)length;
            else if (head == 4)
            {
                frame[1] = 126;
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)length;
            }
            else
            {
                frame[1] = 127;
                for (int i = 0; i < 8; i++) frame[2 + i] = (byte)(length >> (56 - 8 * i));
            }
            int at = head + Encoding.ASCII.GetBytes(prefix, 0, prefix.Length, frame, head);
            at += Encoding.UTF8.GetBytes(json, 0, json.Length, frame, at);
            Encoding.ASCII.GetBytes(suffix, 0, suffix.Length, frame, at);
            return frame;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            WebStudioSocket[] sockets;
            lock (_socketsGate) { _sockets.AddRange(_joining); _joining.Clear(); sockets = _sockets.ToArray(); }
            foreach (WebStudioSocket socket in sockets) socket.Close();
            WebStudioRequest request;
            while (_requests.TryDequeue(out request))
            {
                request.StatusCode = 503;
                request.Response = "{\"error\":\"Server stopped\"}";
                request.Completed.Set();
            }
        }
    }

    /// <summary>
    /// One studio tab's WebSocket (GET /api/ws). It takes <c>{"id", "method", "path", "body"}</c> text messages, run as HTTP
    /// requests are (the main thread's <see cref="WebStudioServer.Pump"/>), and sends <c>{"type": "reply", "id", "status",
    /// "data"}</c> for each, and <c>{"type": "static" | "live", "data"}</c> when the state changes. A thread reads and one
    /// writes: the writer sends the newest static and live state only (a slow tab skips the states it missed), then the
    /// replies and control frames in order.
    /// </summary>
    internal sealed class WebStudioSocket
    {
        const int MaxMessage = 64 << 20;

        private readonly WebStudioServer _server;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private byte[] _early;
        private int _earlyAt, _earlyCount;

        private readonly object _gate = new object();
        private byte[] _static, _live;
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private bool _closing, _closed;

        public WebStudioSocket(WebStudioServer server, TcpClient client, NetworkStream stream, byte[] early, int earlyCount)
        {
            _server = server;
            _client = client;
            _stream = stream;
            _early = early;
            _earlyCount = earlyCount;
        }

        public void Start()
        {
            new Thread(ReadLoop) { IsBackground = true, Name = "IGTAP TAS Web Studio socket read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "IGTAP TAS Web Studio socket write" }.Start();
        }

        public void PushStatic(byte[] frame) { lock (_gate) { _static = frame; Monitor.Pulse(_gate); } }

        public void PushLive(byte[] frame) { lock (_gate) { _live = frame; Monitor.Pulse(_gate); } }

        public void Reply(byte[] frame) { Send(frame); }

        private void Send(byte[] frame)
        {
            lock (_gate)
            {
                if (_closing || _closed) return;
                _queue.Enqueue(frame);
                Monitor.Pulse(_gate);
            }
        }

        /// <summary>Queues a close frame (code, or none) as the last one; the writer closes the connection after sending it.</summary>
        private void SendClose(int code)
        {
            lock (_gate)
            {
                if (_closing || _closed) return;
                _queue.Enqueue(code < 0 ? new byte[] { 0x88, 0 } : new byte[] { 0x88, 2, (byte)(code >> 8), (byte)code });
                _closing = true;
                Monitor.Pulse(_gate);
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                Monitor.Pulse(_gate);
            }
            _server.Leave(this);
            try { _client.Close(); } catch { }
        }

        private void WriteLoop()
        {
            try
            {
                while (true)
                {
                    byte[] staticFrame, liveFrame;
                    byte[][] queued;
                    bool closing;
                    lock (_gate)
                    {
                        while (!_closed && _static == null && _live == null && _queue.Count == 0) Monitor.Wait(_gate);
                        if (_closed) return;
                        staticFrame = _static; liveFrame = _live; _static = null; _live = null;
                        queued = _queue.ToArray(); _queue.Clear();
                        closing = _closing;
                    }
                    if (!closing)
                    {
                        if (staticFrame != null) _stream.Write(staticFrame, 0, staticFrame.Length);
                        if (liveFrame != null) _stream.Write(liveFrame, 0, liveFrame.Length);
                    }
                    foreach (byte[] frame in queued) _stream.Write(frame, 0, frame.Length);
                    if (closing) break;
                }
            }
            catch (Exception) { }
            Close();
        }

        private void ReadLoop()
        {
            try
            {
                var message = new MemoryStream();
                bool inMessage = false;
                byte[] head = new byte[8];
                while (true)
                {
                    ReadExactly(head, 2);
                    bool fin = (head[0] & 0x80) != 0;
                    int opcode = head[0] & 0x0F;
                    bool masked = (head[1] & 0x80) != 0;
                    long length = head[1] & 0x7F;
                    if (length == 126) { ReadExactly(head, 2); length = (head[0] << 8) | head[1]; }
                    else if (length == 127)
                    {
                        ReadExactly(head, 8);
                        length = 0;
                        for (int i = 0; i < 8; i++) length = (length << 8) | head[i];
                    }
                    // RFC 6455 5.1: a client masks every frame.
                    if (!masked) { SendClose(1002); return; }
                    if (length < 0 || length > MaxMessage || message.Length + length > MaxMessage) { SendClose(1009); return; }
                    byte[] mask = new byte[4];
                    ReadExactly(mask, 4);
                    byte[] payload = new byte[length];
                    ReadExactly(payload, (int)length);
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    if (opcode == 0x8)
                    {
                        SendClose(payload.Length >= 2 ? (payload[0] << 8) | payload[1] : -1);
                        return;
                    }
                    if (opcode == 0x9)
                    {
                        byte[] pong = new byte[2 + payload.Length];
                        pong[0] = 0x8A;
                        pong[1] = (byte)payload.Length;
                        Buffer.BlockCopy(payload, 0, pong, 2, payload.Length);
                        if (payload.Length <= 125) Send(pong);
                        continue;
                    }
                    if (opcode == 0xA) continue;
                    if (opcode == 0x1 || opcode == 0x2)
                    {
                        if (inMessage) { SendClose(1002); return; }
                        inMessage = true;
                        message.SetLength(0);
                    }
                    else if (opcode != 0x0 || !inMessage) { SendClose(1002); return; }
                    message.Write(payload, 0, payload.Length);
                    if (!fin) continue;
                    inMessage = false;
                    Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
            catch (Exception) { }
            finally
            {
                // A close frame queued here closes the connection once the writer has sent it.
                bool closing;
                lock (_gate) closing = _closing;
                if (!closing) Close();
            }
        }

        /// <summary>A request message: queued for the main thread as an HTTP request is; a malformed one is answered here.</summary>
        private void Handle(string text)
        {
            long id = 0;
            try
            {
                JObject message = JObject.Parse(text);
                id = message.Value<long?>("id") ?? 0;
                string method = (message.Value<string>("method") ?? "GET").ToUpperInvariant();
                string path = message.Value<string>("path") ?? "";
                if (!path.StartsWith("/api/", StringComparison.Ordinal) || path == "/api/ws")
                {
                    Send(WebStudioServer.ReplyFrame(id, 404, "{\"error\":\"Not found\"}"));
                    return;
                }
                JToken body = message["body"];
                string bodyText = body == null || body.Type == JTokenType.Null ? "" : body.ToString(Newtonsoft.Json.Formatting.None);
                _server.Enqueue(new WebStudioRequest { Method = method, Path = path, Body = bodyText, Socket = this, SocketId = id });
            }
            catch (Exception ex)
            {
                Send(WebStudioServer.ReplyFrame(id, 400, Newtonsoft.Json.JsonConvert.SerializeObject(new { error = "Bad message: " + ex.Message })));
            }
        }

        /// <summary>Fills <paramref name="buffer"/>'s first <paramref name="count"/> bytes: what came with the handshake first, then the stream.</summary>
        private void ReadExactly(byte[] buffer, int count)
        {
            int offset = 0;
            if (_early != null)
            {
                int take = Math.Min(count, _earlyCount - _earlyAt);
                Buffer.BlockCopy(_early, _earlyAt, buffer, 0, take);
                _earlyAt += take;
                offset = take;
                if (_earlyAt >= _earlyCount) _early = null;
            }
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }
    }
}
