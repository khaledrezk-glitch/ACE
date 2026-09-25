using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AceRevitMcp.Util;

namespace AceRevitMcp.Bridge
{
    /// <summary>
    /// Tiny HTTP endpoint on localhost only. The MCP server (running under Claude) talks to it.
    ///   GET  /health            -> liveness, no token needed
    ///   POST /command           -> { "command": "...", "args": { ... }, "timeoutSeconds": 120 }
    /// Every POST must carry the header  X-Ace-Token: &lt;token from config.json&gt;.
    /// </summary>
    internal sealed class BridgeServer : IDisposable
    {
        private readonly AceConfig _config;
        private readonly RequestDispatcher _dispatcher;
        private readonly string _revitVersion;
        private HttpListener _listener;

        public BridgeServer(AceConfig config, RequestDispatcher dispatcher, string revitVersion)
        {
            _config = config;
            _dispatcher = dispatcher;
            _revitVersion = revitVersion;
        }

        public bool IsRunning => _listener?.IsListening == true;
        public string Url => $"http://localhost:{_config.Port}/";
        public string LastError { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                _listener = new HttpListener();
                // "localhost" (not 127.0.0.1 or +) is the only prefix Windows lets a non-admin user bind.
                _listener.Prefixes.Add(Url);
                _listener.Start();
                LastError = null;
                Log.Info($"Bridge listening on {Url}");
                _ = Task.Run(AcceptLoop);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error($"Could not start the bridge on {Url}: {ex.Message}. " +
                          $"If another program uses port {_config.Port}, change \"port\" in {AceConfig.FilePath}.");
                _listener = null;
            }
        }

        public void Stop()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            Log.Info("Bridge stopped");
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; } // listener stopped
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";

                if (ctx.Request.HttpMethod == "GET" && path == "/health")
                {
                    await WriteAsync(ctx, 200, new JsonObject
                    {
                        ["ok"] = true,
                        ["service"] = "ace-revit-mcp",
                        ["revitVersion"] = _revitVersion,
                    });
                    return;
                }

                if (ctx.Request.HttpMethod != "POST" || path != "/command")
                {
                    await WriteAsync(ctx, 404, Error("Not found"));
                    return;
                }

                if (!string.Equals(ctx.Request.Headers["X-Ace-Token"], _config.Token, StringComparison.Ordinal))
                {
                    await WriteAsync(ctx, 401, Error($"Invalid or missing token. The MCP server must read {AceConfig.FilePath}."));
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                if (JsonNode.Parse(body) is not JsonObject request || request["command"]?.GetValue<string>() is not string command)
                {
                    await WriteAsync(ctx, 400, Error("Body must be JSON: {\"command\": \"...\", \"args\": {...}}"));
                    return;
                }

                var timeout = TimeSpan.FromSeconds(120);
                if (request["timeoutSeconds"] is JsonValue tv && tv.TryGetValue<double>(out var secs) && secs > 0)
                    timeout = TimeSpan.FromSeconds(Math.Min(secs, 3600));

                try
                {
                    var result = await _dispatcher.EnqueueAsync(command, request["args"] as JsonObject, timeout).ConfigureAwait(false);
                    await WriteAsync(ctx, 200, new JsonObject { ["ok"] = true, ["result"] = result });
                }
                catch (Exception ex)
                {
                    await WriteAsync(ctx, 200, Error(Describe(ex)));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"HTTP handler: {ex}");
                try { await WriteAsync(ctx, 500, Error(ex.Message)); } catch { }
            }
        }

        private static string Describe(Exception ex)
        {
            return ex switch
            {
                CommandException ce => ce.Message,
                TimeoutException te => te.Message,
                Autodesk.Revit.Exceptions.ApplicationException ae => $"{ae.GetType().Name}: {ae.Message}", // Revit API exceptions
                _ => $"{ex.GetType().Name}: {ex.Message}",
            };
        }

        private static JsonObject Error(string message) => new JsonObject { ["ok"] = false, ["error"] = message };

        private static async Task WriteAsync(HttpListenerContext ctx, int status, JsonNode payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }
}
