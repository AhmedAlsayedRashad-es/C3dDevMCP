using System.ComponentModel;
using System.Text.Json.Nodes;
using C3dMCP.Server.Core;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class InstanceTools
{
    [McpServerTool(Name = "c3d_instances"), Description("List the running Civil3D instances that have the C3dMCP host: port, pid, version, drawing, loaded payload, active run. Dead registry entries are pruned. Ask the user which port to use when more than one is listed.")]
    public static string Instances()
    {
        try
        {
            var list = InstanceDiscovery.List(probe: true);
            return Compact.Render(new
            {
                count = list.Count,
                instances = list.Select(i => new
                {
                    port = i.Port, pid = i.Pid, version = i.Version, alive = i.Alive, why = i.Why,
                    drawing = i.Drawing == null ? null : Path.GetFileName(i.Drawing),
                    payload = i.Payload, activeRun = i.ActiveRun, started = i.Started,
                }),
                hint = list.Count == 0 ? "start Civil3D; the C3dMCP palette shows the port" : list.Count > 1 ? "ask the user which port, then pass instance=<port>" : null,
            });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_status"), Description("Status of one instance: drawing, loaded payload, active run, baseline validity, main-thread responsiveness, plus the project's cycle summary.")]
    public static string Status(
        [Description("Port of the Civil3D instance (from c3d_instances or the palette). Optional when exactly one instance runs.")] int? instance = null,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null)
    {
        try
        {
            var inst = InstanceDiscovery.Resolve(instance);
            using var c = new HostClient(inst);
            var st = c.Status();
            object? cycle = null;
            try { var ctx = ProjectStore.Resolve(projectDir); cycle = CycleState.Summary(ctx); } catch { }
            return Compact.Render(new { ok = true, instance = st, cycle });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }
}
