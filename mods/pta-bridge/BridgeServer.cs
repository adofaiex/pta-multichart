using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PtaBridge
{
    /// <summary>一条等待主线程执行的命令。</summary>
    internal sealed class PendingCommand
    {
        public string Cmd;
        public Dictionary<string, object> Args;
        public readonly TaskCompletionSource<object> Tcs = new TaskCompletionSource<object>();
    }

    /// <summary>
    /// HTTP 服务 + 主线程命令队列。
    /// 请求线程只做 JSON 收发，所有 Unity 访问都在 BridgeRunner.Update 里通过 Commands.Execute 执行。
    /// </summary>
    internal static class Bridge
    {
        public const int DefaultPort = 59731;
        public const int CommandTimeoutMs = 15000;

        private static readonly ConcurrentQueue<PendingCommand> Queue = new ConcurrentQueue<PendingCommand>();
        private static readonly object HttpGate = new object();

        private static volatile bool _running;
        private static Thread _thread;
        private static HttpListener _http;
        private static TcpListener _tcp;
        private static int _port = DefaultPort;
        private static string _serverKind = "none";
        private static DateTime _startedUtc = DateTime.UtcNow;

        public static int Port { get { return _port; } }
        public static string ServerKind { get { lock (HttpGate) { return _serverKind; } } }
        public static bool IsRunning { get { return _running; } }
        public static int QueueDepth { get { return Queue.Count; } }
        public static DateTime StartedUtc { get { return _startedUtc; } }

        // ---------- 生命周期 ----------

        public static void Start(int port)
        {
            if (_running)
            {
                BridgeLog.Warn("bridge already running on port " + _port);
                return;
            }

            _port = port;
            _startedUtc = DateTime.UtcNow;
            _running = true;
            _thread = new Thread(ServerLoop);
            _thread.IsBackground = true;
            _thread.Name = "pta-bridge-http";
            _thread.Start();
            BridgeLog.Info("bridge server thread started, target 127.0.0.1:" + port);
        }

        public static void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            BridgeLog.Info("stopping bridge server");

            try { if (_http != null) _http.Stop(); } catch { }
            try { if (_http != null) _http.Close(); } catch { }
            try { if (_tcp != null) _tcp.Stop(); } catch { }
            _http = null;
            _tcp = null;

            // 把队列里还没执行的请求都拒绝掉，避免请求线程干等 15s
            PendingCommand leftover;
            while (Queue.TryDequeue(out leftover))
            {
                try
                {
                    leftover.Tcs.TrySetException(new InvalidOperationException("bridge stopped"));
                }
                catch { }
            }
        }

        private static void ServerLoop()
        {
            for (int attempt = 1; _running; attempt++)
            {
                if (TryStartHttpListener())
                {
                    AcceptHttpLoop();
                    return;
                }

                if (!_running) return;

                if (TryStartTcpListener())
                {
                    AcceptTcpLoop();
                    return;
                }

                if (attempt >= 10)
                {
                    BridgeLog.Error("cannot bind 127.0.0.1:" + _port + " (HttpListener and TcpListener both failed) - giving up");
                    _running = false;
                    return;
                }

                BridgeLog.Warn("bind 127.0.0.1:" + _port + " failed, retry " + attempt + "/10 in 3s");
                Thread.Sleep(3000);
            }
        }

        // ---------- 主线程泵 ----------

        /// <summary>只在主线程（BridgeRunner.Update）调用。</summary>
        public static void Pump()
        {
            int budget = 16;
            PendingCommand item;
            while (budget-- > 0 && Queue.TryDequeue(out item))
            {
                Task<object> task = item.Tcs.Task;
                if (task.IsCompleted)
                {
                    // 客户端已经超时放弃，别再执行了（可能带副作用的命令照旧？——不执行，避免事后写入）
                    continue;
                }

                try
                {
                    long started = DateTime.UtcNow.Ticks;
                    object result = Commands.Execute(item.Cmd, item.Args);
                    double ms = (DateTime.UtcNow.Ticks - started) / 10000.0;
                    if (ms > 500) BridgeLog.Info("cmd '" + item.Cmd + "' took " + ms.ToString("F0") + "ms");
                    item.Tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    BridgeLog.Error("cmd '" + item.Cmd + "' failed", ex);
                    item.Tcs.TrySetException(ex);
                }
            }
        }

        // ---------- 请求处理 ----------

        private static string HandleRequest(string method, string path, string body)
        {
            try
            {
                string p = string.IsNullOrEmpty(path) ? "/" : path.Trim();
                int q = p.IndexOf('?');
                if (q >= 0) p = p.Substring(0, q);

                if (p.Equals("/ping", StringComparison.OrdinalIgnoreCase) || p.Equals("/", StringComparison.OrdinalIgnoreCase))
                {
                    return Envelope("ping", new Dictionary<string, object>());
                }

                if (p.Equals("/cmd", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(body)) return EnvelopeError("empty request body (POST JSON {\"cmd\":...})");

                    object parsed;
                    try
                    {
                        parsed = Json.Parse(body);
                    }
                    catch (Exception ex)
                    {
                        return EnvelopeError("bad JSON: " + ex.Message);
                    }

                    var dict = parsed as Dictionary<string, object>;
                    if (dict == null) return EnvelopeError("request body must be a JSON object");

                    string cmd = Json.Str(dict, "cmd", null);
                    if (string.IsNullOrEmpty(cmd)) return EnvelopeError("missing 'cmd'");

                    var args = Json.Get(dict, "args") as Dictionary<string, object>;
                    if (args == null) args = new Dictionary<string, object>();

                    return Envelope(cmd, args);
                }

                return EnvelopeError("unknown path '" + p + "' (use GET /ping or POST /cmd)");
            }
            catch (Exception ex)
            {
                BridgeLog.Error("request handling failed", ex);
                return EnvelopeError("bridge: " + ex.Message);
            }
        }

        private static string Envelope(string cmd, Dictionary<string, object> args)
        {
            if (!_running) return EnvelopeError("bridge is not running");

            var item = new PendingCommand { Cmd = cmd, Args = args };
            Queue.Enqueue(item);

            Task<object> task = item.Tcs.Task;
            bool done;
            try
            {
                done = task.Wait(CommandTimeoutMs);
            }
            catch (Exception)
            {
                done = task.IsCompleted;
            }

            if (!done)
            {
                return EnvelopeError("timeout after " + CommandTimeoutMs + "ms waiting for Unity main thread (cmd '" + cmd + "')");
            }

            if (task.IsFaulted)
            {
                Exception inner = null;
                try { inner = task.Exception == null ? null : task.Exception.GetBaseException(); } catch { }
                if (inner is BridgeError) return EnvelopeError(inner.Message);
                if (inner != null) return EnvelopeError(inner.GetType().Name + ": " + inner.Message);
                return EnvelopeError("command failed");
            }

            if (task.IsCanceled) return EnvelopeError("command cancelled");

            object result;
            try { result = task.Result; }
            catch (Exception ex) { return EnvelopeError(ex.Message); }

            var ok = new Dictionary<string, object>();
            ok["ok"] = true;
            ok["result"] = result;
            return Json.Serialize(ok);
        }

        private static string EnvelopeError(string message)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message ?? "unknown error";
            return Json.Serialize(d);
        }

        // ---------- HttpListener 实现 ----------

        private static bool TryStartHttpListener()
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                listener.Prefixes.Add("http://localhost:" + _port + "/");
                listener.Start();
                _http = listener;
                lock (HttpGate) { _serverKind = "HttpListener"; }
                BridgeLog.Info("listening on http://127.0.0.1:" + _port + "/ (HttpListener)");
                return true;
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("HttpListener unavailable (" + ex.GetType().Name + ": " + ex.Message + "), trying TcpListener");
                try { if (_http != null) _http.Close(); } catch { }
                _http = null;
                return false;
            }
        }

        private static void AcceptHttpLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _http.GetContext();
                }
                catch (Exception ex)
                {
                    if (!_running) return;
                    BridgeLog.Warn("HttpListener.GetContext failed: " + ex.Message);
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    ThreadPool.QueueUserWorkItem(delegate (object state) { HandleHttpContext(state); }, ctx);
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("cannot queue http context: " + ex.Message);
                }
            }
        }

        private static void HandleHttpContext(object state)
        {
            var ctx = state as HttpListenerContext;
            if (ctx == null) return;

            string json;
            try
            {
                string body = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        if (ctx.Request.HasEntityBody)
                        {
                            ctx.Request.InputStream.CopyTo(ms);
                            body = Encoding.UTF8.GetString(ms.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("reading request body failed: " + ex.Message);
                }

                string path = ctx.Request.Url != null ? ctx.Request.Url.AbsolutePath : "/";
                json = HandleRequest(ctx.Request.HttpMethod, path, body);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("HandleHttpContext failed", ex);
                json = EnvelopeError("bridge: " + ex.Message);
            }

            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(json);
                HttpListenerResponse res = ctx.Response;
                res.StatusCode = 200;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = payload.Length;
                res.KeepAlive = false;
                res.AddHeader("Cache-Control", "no-store");
                res.OutputStream.Write(payload, 0, payload.Length);
                res.OutputStream.Flush();
                res.Close();
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("writing http response failed: " + ex.Message);
            }
        }

        // ---------- TcpListener 兜底实现（HttpListener 需要 URL ACL / 管理员时用） ----------

        private static bool TryStartTcpListener()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _tcp = listener;
                lock (HttpGate) { _serverKind = "TcpListener"; }
                BridgeLog.Info("listening on http://127.0.0.1:" + _port + "/ (TcpListener fallback)");
                return true;
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("TcpListener unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
                try { if (_tcp != null) _tcp.Stop(); } catch { }
                _tcp = null;
                return false;
            }
        }

        private static void AcceptTcpLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _tcp.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (!_running) return;
                    BridgeLog.Warn("TcpListener.AcceptTcpClient failed: " + ex.Message);
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    ThreadPool.QueueUserWorkItem(delegate (object state) { HandleTcpClient(state); }, client);
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("cannot queue tcp client: " + ex.Message);
                    try { client.Close(); } catch { }
                }
            }
        }

        private static void HandleTcpClient(object state)
        {
            var client = state as TcpClient;
            if (client == null) return;

            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 20000;
                    client.SendTimeout = 20000;

                    NetworkStream stream = client.GetStream();
                    string requestLine = ReadLine(stream);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] parts = requestLine.Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "GET";
                    string path = parts.Length > 1 ? parts[1] : "/";

                    int contentLength = 0;
                    bool expectContinue = false;

                    while (true)
                    {
                        string line = ReadLine(stream);
                        if (string.IsNullOrEmpty(line)) break;
                        int idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        string name = line.Substring(0, idx).Trim();
                        string value = line.Substring(idx + 1).Trim();

                        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(value, out contentLength);
                        }
                        else if (string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase) &&
                                 value.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            expectContinue = true;
                        }
                    }

                    if (expectContinue)
                    {
                        byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        stream.Write(cont, 0, cont.Length);
                        stream.Flush();
                    }

                    string body = null;
                    if (contentLength > 0)
                    {
                        byte[] buffer = new byte[contentLength];
                        int read = 0;
                        while (read < contentLength)
                        {
                            int n = stream.Read(buffer, read, contentLength - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read > 0) body = Encoding.UTF8.GetString(buffer, 0, read);
                    }

                    string json = HandleRequest(method, path, body);
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    string head =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        "Content-Length: " + payload.Length + "\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("tcp client handling failed: " + ex.Message);
                try { client.Close(); } catch { }
            }
        }

        private static string ReadLine(Stream stream)
        {
            var sb = new StringBuilder(128);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length -= 1;
                    return sb.ToString();
                }
                if (sb.Length > 8192) return sb.ToString();
                sb.Append((char)b);
            }
        }
    }

    /// <summary>挂在 DontDestroyOnLoad 对象上的主线程泵。</summary>
    public sealed class BridgeRunner : MonoBehaviour
    {
        private void Awake()
        {
            try { DontDestroyOnLoad(gameObject); } catch { }
        }

        private void Update()
        {
            try
            {
                Bridge.Pump();

                if (Main.QuitDue)
                {
                    BridgeLog.Info("Application.Quit()");
                    Application.Quit();
                }
            }
            catch (Exception ex)
            {
                // 绝不让异常冒到 Unity 主循环
                BridgeLog.Error("bridge pump crashed", ex);
            }
        }
    }
}
