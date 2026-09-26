using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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
    }

    internal sealed class WebStudioServer : IDisposable
    {
        public const int Port = 17426;

        public static string Url => "http://127.0.0.1:" + Port + "/";

        private readonly ConcurrentQueue<WebStudioRequest> _requests = new ConcurrentQueue<WebStudioRequest>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private string _staticRoot;

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

        public void Pump(Func<WebStudioRequest, object> handler)
        {
            WebStudioRequest request;
            while (_requests.TryDequeue(out request))
            {
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
                    if (!request.AsyncHandling) request.Completed.Set();
                }
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
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 5000;
                string header = ReadHeader(stream);
                if (string.IsNullOrEmpty(header)) return;
                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length < 2) { WriteResponse(stream, 400, "application/json", "{\"error\":\"Bad request\"}"); return; }
                string method = first[0].ToUpperInvariant();
                string path = first[1].Split('?')[0];
                int contentLength = 0;
                foreach (string line in lines)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.Substring(line.IndexOf(':') + 1).Trim(), out contentLength);
                string body = ReadBody(stream, contentLength);

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
                if (!request.Completed.WaitOne(600000))
                { WriteResponse(stream, 503, "application/json", "{\"error\":\"Game main thread did not respond within timeout\"}"); return; }
                WriteResponse(stream, request.StatusCode, "application/json; charset=utf-8", request.Response ?? "{}");
            }
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

        private static string ReadHeader(NetworkStream stream)
        {
            MemoryStream buffer = new MemoryStream();
            int matched = 0;
            while (buffer.Length < 32768)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                buffer.WriteByte((byte)value);
                matched = (matched == 0 && value == '\r') || (matched == 2 && value == '\r') ? matched + 1
                    : (matched == 1 && value == '\n') || (matched == 3 && value == '\n') ? matched + 1 : 0;
                if (matched == 4) break;
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string ReadBody(NetworkStream stream, int length)
        {
            if (length <= 0) return "";
            byte[] bytes = new byte[length];
            int offset = 0;
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
            if (extension == ".ico") return "image/x-icon";
            if (extension == ".woff2") return "font/woff2";
            return "application/octet-stream";
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            WebStudioRequest request;
            while (_requests.TryDequeue(out request)) { request.StatusCode = 503; request.Response = "{\"error\":\"Server stopped\"}"; request.Completed.Set(); }
        }
    }
}
