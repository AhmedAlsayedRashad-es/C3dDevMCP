using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace C3dMCP.Host
{
    /// <summary>A thrown error becomes {error, code} with the given HTTP status.</summary>
    public sealed class HttpError : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public HttpError(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    public sealed class HttpRequest
    {
        public HttpListenerContext Raw;
        public Match Route;
        public string Body;
        public string Query(string name, string fallback = null) => Raw.Request.QueryString[name] ?? fallback;
        public T Json<T>() => string.IsNullOrWhiteSpace(Body) ? default(T) : JsonSerializer.Deserialize<T>(Body, HttpApi.JsonOptions);
    }

    /// <summary>System.Net.HttpListener with one accept thread and thread-pool handlers. No
    /// async/await: inside acad the AutoCAD SynchronizationContext delivers continuations through
    /// the command queue, so handlers block on events instead. Every request needs the instance
    /// token. Routes are (method, regex) → handler returning an object to serialize.</summary>
    public sealed class HttpApi : IDisposable
    {
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpListener _listener;
        private readonly string _token;
        private readonly List<Tuple<string, Regex, Func<HttpRequest, object>>> _routes = new List<Tuple<string, Regex, Func<HttpRequest, object>>>();
        private Thread _accept;
        private volatile bool _running;

        public HttpApi(HttpListener listener, string token)
        {
            _listener = listener;
            _token = token;
        }

        public void Map(string method, string pattern, Func<HttpRequest, object> handler)
        {
            _routes.Add(Tuple.Create(method.ToUpperInvariant(), new Regex("^" + pattern + "$", RegexOptions.Compiled), handler));
        }

        public void Start()
        {
            _running = true;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "C3dMCP.HttpApi.accept" };
            _accept.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception ex)
                {
                    if (_running) HostLog.Write("listener accept failed: " + ex.Message);
                    if (!_running || !_listener.IsListening) return;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                if (!string.Equals(req.Headers["X-C3dMCP-Token"], _token, StringComparison.Ordinal))
                    throw new HttpError(401, "unauthorized", "missing or wrong X-C3dMCP-Token");

                var path = req.Url.AbsolutePath;
                Match match = null;
                Func<HttpRequest, object> handler = null;
                bool pathMatched = false;
                foreach (var r in _routes)
                {
                    var m = r.Item2.Match(path);
                    if (!m.Success) continue;
                    pathMatched = true;
                    if (r.Item1 != req.HttpMethod.ToUpperInvariant()) continue;
                    match = m; handler = r.Item3; break;
                }
                if (handler == null)
                    throw new HttpError(pathMatched ? 405 : 404, pathMatched ? "method-not-allowed" : "not-found", path);

                string body = null;
                if (req.HasEntityBody)
                    using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        body = sr.ReadToEnd();

                var result = handler(new HttpRequest { Raw = ctx, Route = match, Body = body });
                if (result is RawResponse raw) { raw.Write(res); return; }
                WriteJson(res, result is int code ? code : 200, result);
            }
            catch (HttpError he)
            {
                WriteJson(res, he.Status, new { error = he.Message, code = he.Code });
            }
            catch (Exception ex)
            {
                HostLog.Write("request failed " + req.HttpMethod + " " + req.Url.AbsolutePath + ": " + ex.Message);
                WriteJson(res, 500, new { error = ex.Message, code = "internal" });
            }
        }

        public static void WriteJson(HttpListenerResponse res, int status, object payload)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
                res.StatusCode = status;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }

        /// <summary>A handler may return this to take over the response (e.g. a status code + object).</summary>
        public sealed class RawResponse
        {
            public int Status = 200;
            public object Payload;
            public void Write(HttpListenerResponse res) => WriteJson(res, Status, Payload);
        }

        public static RawResponse Status(int status, object payload) => new RawResponse { Status = status, Payload = payload };

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
