using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace C3dMCP.Server.Core;

/// <summary>The HTTP client for one instance. Every call carries the instance token. A non-2xx
/// answer becomes a ToolError with the host's code.</summary>
public sealed class HostClient : IDisposable
{
    private readonly HttpClient _http;
    public InstanceInfo Instance { get; }

    public HostClient(InstanceInfo instance)
    {
        Instance = instance;
        _http = new HttpClient { BaseAddress = new Uri("http://localhost:" + instance.Port + "/"), Timeout = TimeSpan.FromSeconds(70) };
        _http.DefaultRequestHeaders.Add("X-C3dMCP-Token", instance.Token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public JsonNode Status(int timeoutMs = 5000) => Get("v1/status", timeoutMs);
    public JsonNode Runs() => Get("v1/runs");
    public JsonNode Run(string runId) => Get("v1/runs/" + runId);
    public JsonNode Wait(string runId, int timeoutSec, long sinceSeq) => Get($"v1/runs/{runId}/wait?timeoutSec={timeoutSec}&sinceSeq={sinceSeq}", (timeoutSec + 15) * 1000);
    public JsonNode Reload(string dll, string addinName) => Post("v1/reload", new { dll, addinName }, 6 * 60 * 1000);
    public JsonNode StartRun(string runId, string runDir, string command, Dictionary<string, string>? args, string? taskId, int maxDrainSeconds)
        => Post("v1/runs", new { runId, runDir, command, args, taskId, maxDrainSeconds });
    public JsonNode Complete(string runId, string status, string by, string? reason) => Post($"v1/runs/{runId}/complete", new { status, by, reason });
    public JsonNode IdlePing() => Post("v1/idle-ping", new { });
    public JsonNode Baseline() => Post("v1/baseline", new { });
    public JsonNode Reset(bool hard) => Post("v1/reset", new { hard }, 6 * 60 * 1000);
    public JsonNode HostLog(int tail) => Get("v1/log?tail=" + tail);
    public JsonNode Escalation(object? card) => card == null ? Delete("v1/escalation") : Post("v1/escalation", card);

    private JsonNode Delete(string path, int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var res = _http.DeleteAsync(path, cts.Token).GetAwaiter().GetResult();
        return Parse(res);
    }

    private JsonNode Get(string path, int timeoutMs = 30000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var res = _http.GetAsync(path, cts.Token).GetAwaiter().GetResult();
        return Parse(res);
    }

    private JsonNode Post(string path, object body, int timeoutMs = 30000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var content = new StringContent(JsonSerializer.Serialize(body, Compact.Json), Encoding.UTF8, "application/json");
        using var res = _http.PostAsync(path, content, cts.Token).GetAwaiter().GetResult();
        return Parse(res);
    }

    private JsonNode Parse(HttpResponseMessage res)
    {
        var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch { }
        if (!res.IsSuccessStatusCode)
        {
            var code = node?["code"]?.GetValue<string>() ?? ("http-" + (int)res.StatusCode);
            var msg = node?["error"]?.GetValue<string>() ?? text;
            throw new ToolError(code, msg, extra: new { port = Instance.Port, http = (int)res.StatusCode });
        }
        return node ?? new JsonObject();
    }

    public void Dispose() => _http.Dispose();
}
