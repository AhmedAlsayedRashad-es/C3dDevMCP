using System.Text.Json;
using System.Text.Json.Serialization;

namespace C3dMCP.Server.Core;

/// <summary>cycle.json: where the loop is, on disk, never in the chat. Tools advance it; hooks
/// read it. A ROUND is one plan → implement → run → analyze pass ending in a verdict.</summary>
public sealed class Cycle
{
    public string? TaskId { get; set; }
    public string State { get; set; } = "idle";      // idle | planning | implementing | waiting-run | analyzing | escalated | done
    public string? Problem { get; set; }
    public int Rung { get; set; } = 1;
    public int Round { get; set; }
    public int FailedRounds { get; set; }
    public int Redispatch { get; set; }
    public int StepIndex { get; set; }
    public string? LastRun { get; set; }
    public string? LastVerdict { get; set; }
    public bool LastPlanBlocked { get; set; }
    public string? LastPlanSummary { get; set; }
    public Escalation? EscalationOpen { get; set; }
    public Dictionary<int, bool> HumanApproved { get; set; } = new();
    public Dictionary<string, int> SolvedAt { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public List<RoundRecord> Rounds { get; set; } = new();
    public List<string> History { get; set; } = new();
    public string? RunThisCycle { get; set; }        // set by c3d_run, cleared when the analyzer records a verdict
    public DateTime UpdatedAt { get; set; }

    public sealed class RoundRecord
    {
        public int Number { get; set; }
        public int Rung { get; set; }
        public string? RunId { get; set; }
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
        public List<string> FailedChecks { get; set; } = new();
        public DateTime At { get; set; }
    }

    public sealed class Escalation
    {
        public string Id { get; set; } = "";
        public string Why { get; set; } = "";
        public List<RoundRecord> Rounds { get; set; } = new();
        public List<string> Options { get; set; } = new() { "climb", "retry", "new-approach", "stop" };
        public DateTime OpenedAt { get; set; }
    }
}

public static class CycleState
{
    /// <summary>Best effort: show or clear the escalation card in the palette of the instance
    /// that matches the project's Civil3D version (when exactly one is running).</summary>
    public static void PushEscalation(ProjectContext ctx, Cycle c)
    {
        try
        {
            var inst = InstanceDiscovery.List(probe: false).Where(i => string.Equals(i.Version, ctx.Version)).ToList();
            if (inst.Count != 1) return;
            using var client = new HostClient(inst[0]);
            var e = c.EscalationOpen;
            if (e == null) { client.Escalation(null); return; }
            var r1 = e.Rounds.Count > 0 ? e.Rounds[^Math.Min(2, e.Rounds.Count)] : null;
            var r2 = e.Rounds.Count > 1 ? e.Rounds[^1] : null;
            client.Escalation(new
            {
                id = e.Id, title = "Escalation " + e.Id + ": " + e.Why,
                round1Reason = r1?.Reason ?? "", round1Checks = r1 == null ? "" : string.Join(", ", r1.FailedChecks),
                round2Reason = r2?.Reason ?? "", round2Checks = r2 == null ? "" : string.Join(", ", r2.FailedChecks),
            });
        }
        catch { }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Cycle Load(ProjectContext ctx)
    {
        try { if (File.Exists(ctx.CycleFile)) return JsonSerializer.Deserialize<Cycle>(File.ReadAllText(ctx.CycleFile), Json) ?? new Cycle(); } catch { }
        return new Cycle();
    }

    public static void Save(ProjectContext ctx, Cycle c)
    {
        c.UpdatedAt = DateTime.UtcNow;
        if (c.History.Count > 60) c.History.RemoveRange(0, c.History.Count - 60);
        Directory.CreateDirectory(ctx.StoreDir);
        File.WriteAllText(ctx.CycleFile, JsonSerializer.Serialize(c, Json));
    }

    private static void Note(Cycle c, string what) => c.History.Add(DateTime.UtcNow.ToString("HH:mm:ss") + " " + what);

    public static void OnNewTask(ProjectContext ctx, string taskId)
    {
        var p = PipelineConfig.Load(ctx);
        var c = Load(ctx);
        var keep = c.Constraints;
        c = new Cycle { TaskId = taskId, State = "planning", Rung = p.Ladder.StartRung, Constraints = keep };
        Note(c, "task " + taskId + " active, rung " + c.Rung);
        Save(ctx, c);
    }

    public static void OnRunStarted(ProjectContext ctx, string taskId, string runId, string command)
    {
        var c = Load(ctx);
        c.TaskId ??= taskId;
        c.LastRun = runId; c.RunThisCycle = runId;
        if (c.State is "planning" or "implementing" or "idle") c.State = "waiting-run";
        Note(c, "run " + runId + " " + command);
        Save(ctx, c);
    }

    public static object Summary(ProjectContext ctx)
    {
        var c = Load(ctx);
        var p = PipelineConfig.Load(ctx);
        return new { c.TaskId, c.State, c.Rung, c.Round, c.FailedRounds, c.LastRun, c.LastVerdict, escalation = c.EscalationOpen?.Id, next = Next(c, p) };
    }

    /// <summary>The step the cycle is on, resolved to an agent, or the human when the rung asks.</summary>
    public static object? Next(Cycle c, PipelineConfig p)
    {
        if (c.State == "escalated") return new { step = "escalation", askHuman = true, hint = "ask the user with AskUserQuestion, then c3d_escalation_resolve" };
        if (c.State == "done" || c.State == "idle") return null;
        var rung = p.GetRung(c.Rung);
        if (rung == null) return null;
        if (rung.AskHuman && !(c.HumanApproved.TryGetValue(c.Rung, out var ok) && ok))
            return new { step = "human-approval", askHuman = true, rung = c.Rung, hint = "rung " + c.Rung + " needs the user's approval: ask, then c3d_cycle_record kind=approve" };
        var steps = rung.Steps.Where(s => s.Active).ToList();
        if (c.StepIndex >= steps.Count) return new { step = "verdict", hint = "record the analyzer verdict with c3d_cycle_record kind=verdict" };
        var s = steps[c.StepIndex];
        var (agent, model, effort) = p.Resolve(s);
        return new { step = s.Use, agent, model, effort, optional = s.Optional, index = c.StepIndex, of = steps.Count };
    }

    /// <summary>Agents allowed to be dispatched now: the current step, every optional active step
    /// of this rung, and every step already passed (re-dispatch of the same plan counts).</summary>
    public static HashSet<string> AllowedAgents(Cycle c, PipelineConfig p)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rung = p.GetRung(c.Rung);
        if (rung == null) return set;
        var steps = rung.Steps.Where(s => s.Active).ToList();
        for (int i = 0; i < steps.Count; i++)
        {
            if (i == c.StepIndex || steps[i].Optional) set.Add(p.Resolve(steps[i]).agent);
        }
        return set;
    }

    // ---- events --------------------------------------------------------------------------------

    public sealed class Event
    {
        public string Kind { get; set; } = "";       // step-done | plan | implemented | verdict | redispatch | approve | escalate | reset | problem | constraint
        public string? Agent { get; set; }
        public string? Summary { get; set; }
        public bool Blocked { get; set; }
        public string? RunId { get; set; }
        public string? Verdict { get; set; }        // pass | partial | fail
        public string? Reason { get; set; }
        public List<string>? FailedChecks { get; set; }
        public bool Progress { get; set; }
        public string? Problem { get; set; }
        public string? Constraint { get; set; }
    }

    public static Cycle Apply(ProjectContext ctx, Event e)
    {
        var p = PipelineConfig.Load(ctx);
        var c = Load(ctx);
        var rung = p.GetRung(c.Rung) ?? p.GetRung(p.Ladder.StartRung)!;
        var steps = rung.Steps.Where(s => s.Active).ToList();
        switch (e.Kind)
        {
            case "step-done":
            {
                // advance past the step this agent belongs to (and any optional steps before it)
                int idx = steps.FindIndex(c.StepIndex, s => string.Equals(p.Resolve(s).agent, e.Agent, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) c.StepIndex = idx + 1;
                var agentName = (e.Agent ?? "").ToLowerInvariant();
                if (agentName.Contains("planner")) { c.State = "implementing"; c.LastPlanBlocked = e.Blocked; c.LastPlanSummary = e.Summary; }
                else if (agentName.Contains("implementer")) c.State = c.RunThisCycle != null ? "analyzing" : "implementing";
                else if (agentName.Contains("analyzer")) c.State = "analyzing";
                Note(c, "step done: " + e.Agent + (e.Blocked ? " (blocked)" : ""));
                break;
            }
            case "plan":
                c.LastPlanBlocked = e.Blocked; c.LastPlanSummary = e.Summary; c.State = e.Blocked ? "planning" : "implementing";
                Note(c, "plan " + (e.Blocked ? "BLOCKED: " : "") + e.Summary);
                break;
            case "implemented":
                c.State = c.RunThisCycle != null ? "analyzing" : "implementing";
                Note(c, "implemented: " + e.Summary);
                break;
            case "redispatch":
                c.Redispatch++;
                Note(c, "redispatch " + c.Redispatch + "/" + p.Ladder.RedispatchCeiling + ": " + e.Reason);
                if (c.Redispatch >= p.Ladder.RedispatchCeiling) OpenEscalation(c, "redispatch ceiling reached: " + e.Reason);
                break;
            case "approve":
                c.HumanApproved[c.Rung] = true; Note(c, "user approved rung " + c.Rung);
                break;
            case "problem":
                c.Problem = e.Problem; c.FailedRounds = 0; c.Rung = p.Ladder.StartRung; c.StepIndex = 0; c.State = "planning";
                Note(c, "problem: " + e.Problem);
                break;
            case "constraint":
                if (!string.IsNullOrWhiteSpace(e.Constraint)) c.Constraints.Add(e.Constraint!);
                break;
            case "escalate":
                OpenEscalation(c, e.Reason ?? "escalated by the orchestrator");
                break;
            case "reset":
                c.Rung = p.Ladder.StartRung; c.Round = 0; c.FailedRounds = 0; c.Redispatch = 0; c.StepIndex = 0; c.State = "planning"; c.EscalationOpen = null;
                Note(c, "ladder reset: " + e.Reason);
                break;
            case "verdict":
            {
                var v = (e.Verdict ?? "fail").ToLowerInvariant();
                c.Round++;
                var round = new Cycle.RoundRecord { Number = c.Round, Rung = c.Rung, RunId = e.RunId ?? c.LastRun, Verdict = v, Reason = e.Reason, FailedChecks = e.FailedChecks ?? new(), At = DateTime.UtcNow };
                c.Rounds.Add(round);
                c.LastVerdict = v; c.RunThisCycle = null; c.Redispatch = 0; c.StepIndex = 0;
                if (e.RunId != null || c.LastRun != null)
                {
                    var t = ProjectStore.LoadTask(ctx, c.TaskId ?? "");
                    if (t != null) { t.LatestVerdict = v; ProjectStore.SaveTask(ctx, t); }
                }
                if (v == "pass")
                {
                    c.State = "done"; c.FailedRounds = 0;
                    if (c.Problem != null) c.SolvedAt[c.Problem] = c.Rung;
                    Note(c, "round " + c.Round + " PASS at rung " + c.Rung);
                }
                else if (v == "partial" && (e.Progress || p.Ladder.ResetOnPass))
                {
                    c.FailedRounds = 0; c.State = "planning";
                    if (p.Ladder.ResetOnPass) c.Rung = p.Ladder.StartRung;
                    Note(c, "round " + c.Round + " PARTIAL with progress: ladder back to rung " + c.Rung);
                }
                else
                {
                    c.FailedRounds++;
                    Note(c, "round " + c.Round + " " + v.ToUpperInvariant() + " (" + c.FailedRounds + " failed): " + e.Reason);
                    if (c.FailedRounds >= p.Ladder.EscalateAfterFails)
                        OpenEscalation(c, c.FailedRounds + " failed rounds on '" + (c.Problem ?? c.TaskId) + "'");
                    else
                    {
                        if (c.FailedRounds >= p.Ladder.ClimbAfterFails && c.Rung < p.MaxRung && !(p.GetRung(c.Rung + 1)?.AskHuman ?? false))
                        { c.Rung++; Note(c, "climb to rung " + c.Rung); }
                        c.State = "planning";
                    }
                }
                break;
            }
            default:
                throw new ToolError("bad-event", "unknown event kind '" + e.Kind + "'");
        }
        Save(ctx, c);
        if (c.State == "escalated" || e.Kind == "reset") PushEscalation(ctx, c);
        return c;
    }

    private static void OpenEscalation(Cycle c, string why)
    {
        var id = "esc-" + (c.Rounds.Count + 1);
        c.EscalationOpen = new Cycle.Escalation { Id = id, Why = why, Rounds = c.Rounds.TakeLast(2).ToList(), OpenedAt = DateTime.UtcNow };
        c.State = "escalated";
        Note(c, "ESCALATION " + id + ": " + why);
    }

    public static Cycle Resolve(ProjectContext ctx, string escalationId, string decision, string? answer, string? constraint)
    {
        var p = PipelineConfig.Load(ctx);
        var c = Load(ctx);
        if (c.EscalationOpen == null) throw new ToolError("no-escalation", "no escalation is open");
        if (!string.Equals(c.EscalationOpen.Id, escalationId, StringComparison.OrdinalIgnoreCase)) throw new ToolError("wrong-escalation", "open escalation is " + c.EscalationOpen.Id);
        if (!string.IsNullOrWhiteSpace(constraint)) c.Constraints.Add(constraint!);
        switch (decision.ToLowerInvariant())
        {
            case "climb":
                c.Rung = Math.Min(p.MaxRung, c.Rung + 1); c.FailedRounds = 0; c.HumanApproved[c.Rung] = true; c.State = "planning"; c.StepIndex = 0;
                break;
            case "retry":
                c.FailedRounds = Math.Max(0, p.Ladder.EscalateAfterFails - 1); c.State = "planning"; c.StepIndex = 0;   // one more round, then back to the user
                break;
            case "new-approach":
                c.Problem = answer ?? c.Problem; c.FailedRounds = 0; c.Rung = p.Ladder.StartRung; c.State = "planning"; c.StepIndex = 0;
                break;
            case "stop":
                c.State = "idle";
                break;
            default:
                throw new ToolError("bad-decision", "decision must be climb | retry | new-approach | stop");
        }
        Note(c, "escalation " + escalationId + " resolved: " + decision + (answer != null ? " — " + answer : ""));
        c.EscalationOpen = null; c.Redispatch = 0;
        Save(ctx, c);
        PushEscalation(ctx, c);
        return c;
    }
}
