using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C3dMCP.Server.Core;

public sealed class InstanceInfo
{
    public int Pid { get; set; }
    public long ProcessStartTicks { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public string? Version { get; set; }
    public string? Exe { get; set; }
    public string? HostVersion { get; set; }
    public string? Listener { get; set; }
    public DateTime Started { get; set; }
    public DateTime Updated { get; set; }
    public string? Drawing { get; set; }
    public JsonElement? Payload { get; set; }
    public JsonElement? ActiveRun { get; set; }
    [JsonIgnore] public string File { get; set; } = "";
    [JsonIgnore] public bool Alive { get; set; }
    [JsonIgnore] public string? Why { get; set; }
}

/// <summary>Finds live Civil3D instances from instances\&lt;pid&gt;.json. A file is trusted only when
/// a process with that pid exists AND its start time matches (pid reuse guard); stale files are
/// deleted. "Alive" also requires the HTTP status call to answer.</summary>
public static class InstanceDiscovery
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static List<InstanceInfo> List(bool probe = true)
    {
        var result = new List<InstanceInfo>();
        if (!Directory.Exists(Paths.Instances)) return result;
        foreach (var file in Directory.GetFiles(Paths.Instances, "*.json"))
        {
            InstanceInfo? info;
            try { info = JsonSerializer.Deserialize<InstanceInfo>(System.IO.File.ReadAllText(file), Json); }
            catch { continue; }
            if (info == null) continue;
            info.File = file;
            if (!ProcessMatches(info.Pid, info.ProcessStartTicks))
            {
                try { System.IO.File.Delete(file); } catch { }
                continue;
            }
            if (probe)
            {
                try
                {
                    using var c = new HostClient(info);
                    c.Status(1500);
                    info.Alive = true;
                }
                catch (Exception ex) { info.Alive = false; info.Why = ex.Message; }
            }
            else info.Alive = true;
            result.Add(info);
        }
        return result.OrderBy(i => i.Port).ToList();
    }

    public static bool ProcessMatches(int pid, long startTicks)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (startTicks == 0) return true;
            var ticks = p.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(ticks - startTicks) < TimeSpan.TicksPerSecond;
        }
        catch { return false; }
    }

    /// <summary>Pick the instance for a tool call: by port when given; else the only live one;
    /// else an error that lists the candidates.</summary>
    public static InstanceInfo Resolve(int? port)
    {
        var all = List(probe: false);
        if (port is > 0)
        {
            var hit = all.FirstOrDefault(i => i.Port == port);
            if (hit != null) return hit;
            throw new ToolError("no-instance", "no Civil3D instance is listening on port " + port + "; run c3d_instances and ask the user which port to use",
                extra: new { instances = all.Select(i => new { i.Port, i.Pid, i.Version, i.Drawing }) });
        }
        if (all.Count == 1) return all[0];
        if (all.Count == 0) throw new ToolError("no-instance", "no Civil3D instance with the C3dMCP host is running; start Civil3D and check the palette");
        throw new ToolError("ambiguous-instance", all.Count + " instances are running; pass the port the user gave you",
            extra: new { instances = all.Select(i => new { i.Port, i.Pid, i.Version, i.Drawing }) });
    }
}
