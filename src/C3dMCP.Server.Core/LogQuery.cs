using System.Text.Json;
using System.Text.RegularExpressions;

namespace C3dMCP.Server.Core;

/// <summary>Reads run.jsonl slices from disk: tail, grep (regex, case-insensitive), sinceSeq,
/// src filter, capped count. Never returns the whole file.</summary>
public static class LogQuery
{
    public sealed class Result
    {
        public List<JsonElement> Lines { get; set; } = new();
        public long LastSeq { get; set; }
        public long Total { get; set; }
        public int Matched { get; set; }
        public bool Truncated { get; set; }
        public long? Next { get; set; }
    }

    public static Result Query(string runJsonl, int? tail, string? grep, long? sinceSeq, string? src, int max)
    {
        max = Math.Max(1, Math.Min(200, max));
        var r = new Result();
        if (!File.Exists(runJsonl)) throw new ToolError("no-log", "no run.jsonl at " + runJsonl);
        Regex? rx = string.IsNullOrEmpty(grep) ? null : new Regex(grep, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var matched = new List<(long seq, string line)>();
        using (var fs = new FileStream(runJsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                r.Total++;
                long seq = SeqOf(line);
                r.LastSeq = Math.Max(r.LastSeq, seq);
                if (sinceSeq.HasValue && seq <= sinceSeq.Value) continue;
                if (src != null && !line.Contains("\"src\":\"" + src + "\"", StringComparison.Ordinal)) continue;
                if (rx != null && !rx.IsMatch(line)) continue;
                matched.Add((seq, line));
            }
        }
        r.Matched = matched.Count;
        IEnumerable<(long seq, string line)> pick = matched;
        if (tail is > 0) pick = matched.TakeLast(Math.Min(tail.Value, max));
        else if (matched.Count > max) { pick = matched.Take(max); r.Truncated = true; r.Next = matched[max - 1].seq; }
        foreach (var (_, line) in pick)
        {
            try { r.Lines.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch { }
        }
        if (tail is > 0 && matched.Count > r.Lines.Count) r.Truncated = true;
        return r;
    }

    private static long SeqOf(string line)
    {
        // {"seq":123,...
        int i = line.IndexOf("\"seq\":", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += 6; long v = 0;
        while (i < line.Length && char.IsDigit(line[i])) { v = v * 10 + (line[i] - '0'); i++; }
        return v;
    }

    /// <summary>Counts by src and failed diagnostics, for verdict summaries.</summary>
    public static Dictionary<string, long> Counts(string runJsonl)
    {
        var d = new Dictionary<string, long> { ["log"] = 0, ["diag"] = 0, ["diagFailed"] = 0, ["note"] = 0, ["feedback"] = 0, ["host"] = 0, ["late"] = 0 };
        if (!File.Exists(runJsonl)) return d;
        using var fs = new FileStream(runJsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            foreach (var k in new[] { "log", "diag", "note", "feedback", "host" })
                if (line.Contains("\"src\":\"" + k + "\"", StringComparison.Ordinal)) { d[k]++; break; }
            if (line.Contains("\"src\":\"diag\"", StringComparison.Ordinal) && line.Contains("\"ok\":false", StringComparison.Ordinal)) d["diagFailed"]++;
            if (line.Contains("\"late\":true", StringComparison.Ordinal)) d["late"]++;
        }
        return d;
    }
}
