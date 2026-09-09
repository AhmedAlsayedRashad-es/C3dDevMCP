using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace C3dMCP.Server.Core;

/// <summary>Every tool answer goes through here: strings are cut to 200 chars, arrays to 20
/// items, nesting to 6 levels, and the whole document to ~2 KB, with "more":true when cut.
/// Long data stays on disk and in the palette; the agent asks for slices with c3d_log_query.</summary>
public static class Compact
{
    public const int MaxBytes = 2048;
    public const int MaxString = 200;
    public const int MaxArray = 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Render(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, Json);
        bool cut = false;
        node = Trim(node, 0, ref cut);
        var text = node?.ToJsonString(Json) ?? "null";
        if (text.Length > MaxBytes)
        {
            // Shrink arrays harder, then strings, until it fits.
            int arr = MaxArray, str = MaxString;
            while (text.Length > MaxBytes && (arr > 3 || str > 40))
            {
                arr = Math.Max(3, arr / 2); str = Math.Max(40, str / 2);
                cut = true;
                node = Trim(JsonSerializer.SerializeToNode(value, Json), 0, ref cut, arr, str);
                text = node?.ToJsonString(Json) ?? "null";
            }
        }
        if (cut && node is JsonObject o && !o.ContainsKey("more")) { o["more"] = true; text = o.ToJsonString(Json); }
        return text;
    }

    private static JsonNode? Trim(JsonNode? n, int depth, ref bool cut, int maxArray = MaxArray, int maxString = MaxString)
    {
        switch (n)
        {
            case null: return null;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s) && s.Length > maxString) { cut = true; return JsonValue.Create(s[..maxString] + "…"); }
                return v.DeepClone();
            case JsonArray a:
                if (depth > 6) { cut = true; return JsonValue.Create("[…" + a.Count + "]"); }
                var na = new JsonArray();
                int i = 0;
                foreach (var item in a) { if (i++ >= maxArray) { cut = true; break; } na.Add(Trim(item, depth + 1, ref cut, maxArray, maxString)); }
                return na;
            case JsonObject o:
                if (depth > 6) { cut = true; return JsonValue.Create("{…}"); }
                var no = new JsonObject();
                foreach (var kv in o) no[kv.Key] = Trim(kv.Value, depth + 1, ref cut, maxArray, maxString);
                return no;
            default: return n.DeepClone();
        }
    }

    public static string Error(ToolError e) => Render(new { ok = false, code = e.Code, stage = e.Stage, error = e.Message, extra = e.Extra });
    public static string Error(Exception e) => Render(new { ok = false, code = "internal", error = e.GetType().Name + ": " + e.Message });
}
