using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CSBridge
{
    public class HttpRequest
    {
        public string Method;
        public string Path;
        public string Query;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string Body = "";
    }

    public class HttpResponse
    {
        public int Status;
        public string Body;
        public static HttpResponse Json(int status, string body)
        {
            HttpResponse r = new HttpResponse();
            r.Status = status; r.Body = body;
            return r;
        }
        public static HttpResponse Error(int status, string message)
        {
            return Json(status, JsonWriter.Error(message));
        }
    }

    public delegate HttpResponse Handler(HttpRequest req);

    /// <summary>Tiny HTTP/1.1 server on TcpListener, loopback only. One request per connection.</summary>
    public class HttpServer
    {
        public const int Port = 8080;
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 64 * 1024;

        private TcpListener listener;
        private Thread thread;
        private volatile bool running;
        private readonly Dictionary<string, Handler> routes = new Dictionary<string, Handler>();

        public void Get(string path, Handler h) { routes["GET " + path] = h; }
        public void Post(string path, Handler h) { routes["POST " + path] = h; }

        public bool IsRunning { get { return running; } }

        public void Start()
        {
            if (running) return;
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            running = true;
            thread = new Thread(AcceptLoop);
            thread.IsBackground = true;
            thread.Name = "CSBridge-Http";
            thread.Start();
            BridgeLog.Info("HTTP server listening on 127.0.0.1:" + Port);
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            try { listener.Stop(); } catch (Exception ex) { BridgeLog.Error("Stop listener", ex); }
            BridgeLog.Info("HTTP server stopped");
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    TcpClient c = client;
                    ThreadPool.QueueUserWorkItem(delegate (object o) { Handle(c); });
                }
                catch (Exception ex)
                {
                    if (running) BridgeLog.Error("Accept", ex);
                }
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                NetworkStream stream = client.GetStream();
                HttpResponse resp;
                HttpRequest req = null;
                try
                {
                    req = ReadRequest(stream);
                    resp = req == null ? HttpResponse.Error(400, "Bad request") : Dispatch(req);
                }
                catch (ApiException ex)
                {
                    resp = HttpResponse.Error(ex.Status, ex.Message);
                }
                catch (GameThread.TimeoutException ex)
                {
                    BridgeLog.Error("Timeout", ex);
                    resp = HttpResponse.Error(503, ex.Message);
                }
                catch (Exception ex)
                {
                    BridgeLog.Error("Handler", ex);
                    resp = HttpResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
                }
                if (req != null) BridgeLog.Info(req.Method + " " + req.Path + " -> " + resp.Status);
                Write(stream, resp);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Connection", ex);
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        private HttpResponse Dispatch(HttpRequest req)
        {
            // DNS-rebinding / browser protection: only accept loopback Host, never browser cross-origin requests.
            string host;
            req.Headers.TryGetValue("host", out host);
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port)
                return HttpResponse.Error(403, "Forbidden host");
            if (req.Headers.ContainsKey("origin"))
                return HttpResponse.Error(403, "Browser requests are not allowed");

            GuardEndpoints.Touch(req.Path);
            Handler h;
            if (routes.TryGetValue(req.Method + " " + req.Path, out h)) return h(req);

            foreach (string key in routes.Keys)
                if (key.EndsWith(" " + req.Path)) return HttpResponse.Error(405, "Method not allowed");
            return HttpResponse.Error(404, "Not found: " + req.Path);
        }

        private static HttpRequest ReadRequest(NetworkStream stream)
        {
            byte[] buf = new byte[MaxHeaderBytes];
            int len = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (len == buf.Length) return null;
                int n = stream.Read(buf, len, buf.Length - len);
                if (n <= 0) return null;
                len += n;
                headerEnd = IndexOfHeaderEnd(buf, len);
            }

            string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 3) return null;

            HttpRequest req = new HttpRequest();
            req.Method = first[0].ToUpperInvariant();
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
            }

            string cl;
            int contentLength = 0;
            if (req.Headers.TryGetValue("content-length", out cl)) int.TryParse(cl, out contentLength);
            if (contentLength < 0 || contentLength > MaxBodyBytes) return null;
            if (contentLength > 0)
            {
                byte[] body = new byte[contentLength];
                int have = len - (headerEnd + 4);
                if (have > contentLength) have = contentLength;
                Array.Copy(buf, headerEnd + 4, body, 0, have);
                while (have < contentLength)
                {
                    int n = stream.Read(body, have, contentLength - have);
                    if (n <= 0) return null;
                    have += n;
                }
                req.Body = Encoding.UTF8.GetString(body);
            }
            return req;
        }

        private static int IndexOfHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        private static void Write(NetworkStream stream, HttpResponse resp)
        {
            byte[] body = Encoding.UTF8.GetBytes(resp.Body ?? "");
            string status = resp.Status == 200 ? "OK" : resp.Status == 400 ? "Bad Request" : resp.Status == 403 ? "Forbidden" : resp.Status == 422 ? "Unprocessable Entity"
                : resp.Status == 404 ? "Not Found" : resp.Status == 405 ? "Method Not Allowed" : resp.Status == 503 ? "Service Unavailable" : "Error";
            string head = "HTTP/1.1 " + resp.Status + " " + status + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(body, 0, body.Length);
        }
    }
}
