using System.Text.Json;
using System.Text.Json.Nodes;
using C3dMCP.Server.Core;

namespace C3dMCP.Server;

/// <summary>`c3dmcp hook &lt;event&gt;`: reads the Claude Code hook JSON on stdin, decides from the
/// files on disk, prints a block decision (both the legacy and the current shape) or nothing.
/// Events: pre-agent | pre-message | pre-tool | subagent-stop | stop. Always exits 0: a hook
/// crash must never wedge the session.</summary>
public static class HookCli
{
    public static int Run(string[] args)
    {
        var evt = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            var text = Console.In.ReadToEnd();
            var input = (string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)) ?? new JsonObject();
            var cwd = input["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory;
            ProjectContext? ctx = null;
            try { ctx = ProjectStore.Resolve(cwd); } catch { }
            var cycle = ctx == null ? new Cycle() : CycleState.Load(ctx);
            var pipeline = ctx == null ? PipelineConfig.Default() : PipelineConfig.Load(ctx);

            HookDecider.Decision d;
            switch (evt)
            {
                case "pre-agent": d = ctx == null ? new(false, null) : HookDecider.PreAgent(input, cycle, pipeline); break;
                case "pre-message": d = HookDecider.PreMessage(input, Array.Empty<string>()); break;
                case "pre-tool": d = HookDecider.PreTool(input, cycle, InstanceDiscovery.List(probe: false), ctx?.Dir); break;
                case "stop": d = ctx == null ? new(false, null) : HookDecider.Stop(input, cycle, ctx.Dir); break;
                case "subagent-stop":
                {
                    // Advance the pipeline automatically when a c3d-* sub-agent finishes.
                    var sub = input["subagent_type"]?.GetValue<string>() ?? input["agent_type"]?.GetValue<string>() ?? "";
                    if (ctx != null && sub.StartsWith("c3d-", StringComparison.OrdinalIgnoreCase))
                        CycleState.Apply(ctx, new CycleState.Event { Kind = "step-done", Agent = sub });
                    d = new(false, null);
                    break;
                }
                default: d = new(false, null); break;
            }
            Log(evt, input, d);
            if (d.Block)
            {
                var outNode = new JsonObject
                {
                    ["decision"] = "block",
                    ["reason"] = d.Reason,
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = evt == "stop" ? "Stop" : "PreToolUse",
                        ["permissionDecision"] = "deny",
                        ["permissionDecisionReason"] = d.Reason,
                    },
                };
                Console.Out.Write(outNode.ToJsonString());
            }
            return 0;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Paths.Root, "hooks.log"), DateTime.Now.ToString("s") + " " + evt + " ERROR " + ex.Message + "\n"); } catch { }
            return 0;
        }
    }

    private static void Log(string evt, JsonNode input, HookDecider.Decision d)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var tool = input["tool_name"]?.GetValue<string>() ?? input["subagent_type"]?.GetValue<string>() ?? "";
            var sub = input["tool_input"]?["subagent_type"]?.GetValue<string>();
            File.AppendAllText(Path.Combine(Paths.Root, "hooks.log"), $"{DateTime.Now:s} {evt} {tool} {sub} -> {(d.Block ? "BLOCK " + d.Reason : "allow")}\n");
        }
        catch { }
    }
}
