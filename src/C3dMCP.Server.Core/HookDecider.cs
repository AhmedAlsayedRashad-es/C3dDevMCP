using System.Diagnostics;
using System.Text.Json.Nodes;

namespace C3dMCP.Server.Core;

/// <summary>Pure decisions for the Claude Code hooks. Input: the hook JSON, the cycle, the
/// pipeline, the instances. Output: allow, or block with a reason that names the tool that
/// unblocks. Never touches the network.</summary>
public static class HookDecider
{
    public sealed record Decision(bool Block, string? Reason, string? Advance = null);

    public static Decision PreAgent(JsonNode input, Cycle c, PipelineConfig p)
    {
        var sub = input["tool_input"]?["subagent_type"]?.GetValue<string>() ?? "";
        if (!sub.StartsWith("c3d-", StringComparison.OrdinalIgnoreCase)) return new Decision(false, null);
        if (c.State == "escalated")
            return Block($"cycle is escalated ({c.EscalationOpen?.Id}: {c.EscalationOpen?.Why}); ask the user with AskUserQuestion, then call c3d_escalation_resolve before dispatching {sub}");
        if (c.State is "idle" or "done")
            return Block($"no cycle is open (state {c.State}); call c3d_task_new (or c3d_task_activate) and c3d_cycle_record kind=problem before dispatching {sub}");
        var rung = p.GetRung(c.Rung);
        if (rung == null) return new Decision(false, null);
        if (rung.AskHuman && !(c.HumanApproved.TryGetValue(c.Rung, out var ok) && ok))
            return Block($"rung {c.Rung} needs the user's approval; ask with AskUserQuestion, then c3d_cycle_record kind=approve");
        var allowed = CycleState.AllowedAgents(c, p);
        if (!allowed.Contains(sub))
        {
            var next = CycleState.Next(c, p);
            return Block($"pipeline rung {c.Rung} expects {Describe(next)}; {sub} is not allowed now (allowed: {string.Join(", ", allowed)}). Use c3d_pipeline_set to change the pipeline if that is intended.");
        }
        if (sub.Contains("implementer", StringComparison.OrdinalIgnoreCase))
        {
            if (c.LastPlanBlocked) return Block("the last plan is blocked: " + c.LastPlanSummary + "; resolve it (new plan or escalate) before implementing");
            if (c.Redispatch >= p.Ladder.RedispatchCeiling) return Block($"redispatch ceiling {p.Ladder.RedispatchCeiling} reached for this plan; escalate with c3d_cycle_record kind=escalate");
            if (c.State == "analyzing") return Block($"run {c.RunThisCycle} of this cycle has no verdict yet; dispatch c3d-analyzer and record its verdict with c3d_cycle_record kind=verdict first");
        }
        var model = input["tool_input"]?["model"]?.GetValue<string>();
        var step = rung.Steps.Where(s => s.Active).FirstOrDefault(s => string.Equals(p.Resolve(s).agent, sub, StringComparison.OrdinalIgnoreCase));
        if (step != null && model != null)
        {
            var want = p.Resolve(step).model;
            if (want != null && !string.Equals(want, model, StringComparison.OrdinalIgnoreCase))
                return Block($"pipeline rung {c.Rung} runs {sub} with model {want}; the Agent call passed model {model}. Omit model or match the pipeline (c3d_pipeline_set to change it).");
        }
        return new Decision(false, null);
    }

    public static Decision PreMessage(JsonNode input, IEnumerable<string> runningC3dAgents)
    {
        var to = input["tool_input"]?["to"]?.GetValue<string>() ?? "";
        if (runningC3dAgents.Any(a => string.Equals(a, to, StringComparison.OrdinalIgnoreCase)) || to.StartsWith("c3d-", StringComparison.OrdinalIgnoreCase))
            return Block($"do not redirect a running {to}: a mid-flight message restarts its plan. Let it finish, or TaskStop it and relaunch once with a complete brief.");
        return new Decision(false, null);
    }

    public static Decision PreTool(JsonNode input, Cycle c, List<InstanceInfo> instances, string? projectDir)
    {
        var tool = input["tool_name"]?.GetValue<string>() ?? "";
        var name = tool.StartsWith("mcp__c3dmcp__") ? tool["mcp__c3dmcp__".Length..] : tool;
        var ti = input["tool_input"];
        int? port = ti?["instance"]?.GetValue<int?>();
        if (name is "c3d_run" or "c3d_reset" or "c3d_baseline")
        {
            var live = instances.Where(i => i.ActiveRun != null && (port == null || i.Port == port)).ToList();
            if (live.Count > 0)
            {
                var i = live[0];
                return Block($"instance {i.Port} has run {i.ActiveRun?.GetProperty("runId")} {i.ActiveRun?.GetProperty("state")}; wait with c3d_wait_run, or c3d_unlock with a reason");
            }
        }
        if (name == "c3d_run")
        {
            if (c.State == "escalated") return Block($"cycle is escalated ({c.EscalationOpen?.Id}); resolve it with c3d_escalation_resolve before running");
            var touched = HarnessTouched(projectDir);
            if (touched.Count > 0) return Block("the working tree changes harness files (" + string.Join(", ", touched.Take(5)) + "); the harness is read-only for the loop. Revert them or ask the user.");
        }
        if (name == "c3d_unlock" && string.IsNullOrWhiteSpace(ti?["reason"]?.GetValue<string>()))
            return Block("c3d_unlock needs a reason");
        return new Decision(false, null);
    }

    public static Decision Stop(JsonNode input, Cycle c, string? projectDir)
    {
        if (input["stop_hook_active"]?.GetValue<bool>() == true) return new Decision(false, null);
        if (c.State == "escalated") return new Decision(false, null);
        if (c.State is "planning" or "implementing" or "waiting-run" or "analyzing")
        {
            if (c.RunThisCycle != null && !LedgerTouched(projectDir))
                return Block($"run {c.RunThisCycle} happened this cycle but BACKLOG.md and the pitfall ledger were not updated; write what the run showed, then finish the cycle");
            return Block($"cycle {c.TaskId} is at state {c.State} (rung {c.Rung}, round {c.Round + 1}); continue: dispatch the next pipeline step or record the verdict with c3d_cycle_record");
        }
        return new Decision(false, null);
    }

    private static Decision Block(string reason) => new(true, reason);

    private static string Describe(object? next)
    {
        if (next == null) return "nothing (cycle finished)";
        var t = next.GetType();
        var step = t.GetProperty("step")?.GetValue(next)?.ToString();
        var agent = t.GetProperty("agent")?.GetValue(next)?.ToString();
        return agent != null ? $"{step} ({agent})" : step ?? "?";
    }

    /// <summary>Harness paths changed in the working tree: .claude/, tools/, C3dMCP.* .</summary>
    public static List<string> HarnessTouched(string? projectDir)
    {
        var res = new List<string>();
        if (projectDir == null) return res;
        try
        {
            var psi = new ProcessStartInfo("git", "diff --name-only HEAD") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = projectDir };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Trim().Replace('\\', '/');
                if (f.StartsWith(".claude/") || f.StartsWith("tools/c3d") || f.Contains("/C3dMCP.") || f.StartsWith("C3dMCP.") || f.EndsWith("settings.json") && f.Contains(".claude"))
                    res.Add(f);
            }
        }
        catch { }
        return res;
    }

    /// <summary>BACKLOG.md or the pitfall ledger changed in the last 2 hours (working tree or index).</summary>
    public static bool LedgerTouched(string? projectDir)
    {
        try
        {
            var since = DateTime.UtcNow.AddHours(-2);
            if (projectDir != null)
            {
                var probe = new DirectoryInfo(projectDir);
                for (int i = 0; i < 4 && probe != null; i++, probe = probe.Parent)
                {
                    var f = Path.Combine(probe.FullName, "BACKLOG.md");
                    if (File.Exists(f) && File.GetLastWriteTimeUtc(f) > since) return true;
                }
            }
            var ledgerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "First Option", "Civil3D Add-ins");
            if (Directory.Exists(ledgerRoot))
                foreach (var f in Directory.GetFiles(ledgerRoot, "c3d-pitfalls.md", SearchOption.AllDirectories))
                    if (File.GetLastWriteTimeUtc(f) > since) return true;
        }
        catch { }
        return false;
    }
}
