using System.ComponentModel;
using System.Text.Json;
using C3dMCP.Server.Core;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class CycleTools
{
    [McpServerTool(Name = "c3d_cycle_get"), Description("Where the development cycle is: task, state, rung, round, failed rounds, last run and verdict, open escalation, constraints, and the next step with its agent/model/effort. Call this after a compaction instead of re-reading history.")]
    public static string CycleGet([Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var c = CycleState.Load(ctx);
            var p = PipelineConfig.Load(ctx);
            return Compact.Render(new
            {
                ok = true, c.TaskId, c.State, c.Problem, c.Rung, c.Round, c.FailedRounds, c.Redispatch, c.StepIndex, c.LastRun, c.LastVerdict, c.LastPlanBlocked, c.LastPlanSummary, c.RunThisCycle,
                escalation = c.EscalationOpen == null ? null : new { c.EscalationOpen.Id, c.EscalationOpen.Why, c.EscalationOpen.Options, rounds = c.EscalationOpen.Rounds.Select(r => new { r.Number, r.RunId, r.Verdict, r.Reason, r.FailedChecks }) },
                c.Constraints, next = CycleState.Next(c, p), history = c.History.TakeLast(8),
            });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_cycle_record"), Description("Advance the cycle with an event and get the next step. Kinds: problem (name the problem being worked; resets the ladder), plan (blocked=true when the planner could not plan), implemented, verdict (runId, verdict pass|partial|fail, reason, failedChecks, progress), redispatch (same plan sent again; reason), approve (the user approved this rung), escalate (reason), reset (reason), constraint (a standing rule from the user), step-done (agent). Two failed verdicts on the same problem open an escalation and block the agents until c3d_escalation_resolve.")]
    public static string CycleRecord(
        [Description("problem | plan | implemented | verdict | redispatch | approve | escalate | reset | constraint | step-done")] string kind,
        [Description("For verdict: pass | partial | fail")] string? verdict = null,
        [Description("Run id the verdict is about")] string? runId = null,
        [Description("One-line reason / summary")] string? reason = null,
        [Description("Names of the checks that failed")] string[]? failedChecks = null,
        [Description("For verdict partial: did a check flip from unmet to met?")] bool progress = false,
        [Description("For plan: the planner returned blocked")] bool blocked = false,
        [Description("For problem: the problem statement")] string? problem = null,
        [Description("For constraint: the standing rule text")] string? constraint = null,
        [Description("For step-done: the agent that finished")] string? agent = null,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var c = CycleState.Apply(ctx, new CycleState.Event { Kind = kind, Verdict = verdict, RunId = runId, Reason = reason ?? problem, FailedChecks = failedChecks?.ToList(), Progress = progress, Blocked = blocked, Summary = reason, Problem = problem, Constraint = constraint, Agent = agent });
            var p = PipelineConfig.Load(ctx);
            return Compact.Render(new { ok = true, c.State, c.Rung, c.Round, c.FailedRounds, redispatch = c.Redispatch + "/" + p.Ladder.RedispatchCeiling, escalation = c.EscalationOpen?.Id, next = CycleState.Next(c, p), last = c.History.LastOrDefault() });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_ladder_status"), Description("The ladder in one look: rung, rounds, failed rounds, when it escalates, redispatch count, and the next step.")]
    public static string LadderStatus(string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var c = CycleState.Load(ctx); var p = PipelineConfig.Load(ctx);
            return Compact.Render(new { ok = true, c.Rung, startRung = p.Ladder.StartRung, maxRung = p.MaxRung, c.Round, c.FailedRounds, escalateAt = p.Ladder.EscalateAfterFails, climbAfter = p.Ladder.ClimbAfterFails, redispatch = c.Redispatch + "/" + p.Ladder.RedispatchCeiling, c.State, escalation = c.EscalationOpen?.Id, next = CycleState.Next(c, p) });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_pipeline_get"), Description("The project's pipeline.json: the agent bank, the steps per rung with optional/active/model/effort, and the ladder rule.")]
    public static string PipelineGet(string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var p = PipelineConfig.Load(ctx);
            return Compact.Render(new { ok = true, file = ctx.PipelineFile, p.Bank, rungs = p.Rungs.Select(r => new { r.Number, r.AskHuman, steps = r.Steps.Select(s => s.Use + (s.Active ? "" : " (off)") + (s.Optional ? "?" : "") + (s.Model != null ? "@" + s.Model : "")) }), p.Ladder });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_pipeline_set"), Description("Change one step of one rung (active, optional, model, effort, notes) or the ladder rule, and validate the result. The user owns this file; change it when the user asks or when the pipeline blocks an agreed step.")]
    public static string PipelineSet(
        [Description("Rung number")] int rung,
        [Description("Step name from the bank, e.g. diagnostician")] string? step = null,
        bool? active = null, bool? optional = null, string? model = null, string? effort = null, string? notes = null,
        [Description("Ladder: escalate to the user after this many failed rounds")] int? escalateAfterFails = null,
        [Description("Ladder: climb one rung after this many failed rounds")] int? climbAfterFails = null,
        string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var p = PipelineConfig.Load(ctx);
            var r = p.GetRung(rung) ?? throw new ToolError("no-rung", "rung " + rung + " does not exist");
            if (step != null)
            {
                var s = r.Steps.FirstOrDefault(x => string.Equals(x.Use, step, StringComparison.OrdinalIgnoreCase));
                if (s == null) { if (!p.Bank.ContainsKey(step)) throw new ToolError("no-agent", step + " is not in the bank"); s = new PipelineConfig.Step { Use = step }; r.Steps.Add(s); }
                if (active.HasValue) s.Active = active.Value;
                if (optional.HasValue) s.Optional = optional.Value;
                if (model != null) s.Model = model;
                if (effort != null) s.Effort = effort;
                if (notes != null) s.Notes = notes;
            }
            if (escalateAfterFails.HasValue) p.Ladder.EscalateAfterFails = escalateAfterFails.Value;
            if (climbAfterFails.HasValue) p.Ladder.ClimbAfterFails = climbAfterFails.Value;
            var errors = p.Validate();
            if (errors.Count > 0) throw new ToolError("invalid-pipeline", string.Join("; ", errors));
            PipelineConfig.Save(ctx, p);
            return Compact.Render(new { ok = true, file = ctx.PipelineFile, rung = new { r.Number, steps = r.Steps.Select(s => new { s.Use, s.Active, s.Optional, s.Model, s.Effort }) }, p.Ladder });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_escalation_resolve"), Description("Record the user's decision on an open escalation: climb (next rung, user-approved), retry (one more round with the user's note), new-approach (restate the problem), stop. Optional constraint is added to the standing rules.")]
    public static string EscalationResolve(
        [Description("Escalation id from c3d_cycle_get")] string escalationId,
        [Description("climb | retry | new-approach | stop")] string decision,
        [Description("The user's words (note, or the new problem statement)")] string? answer = null,
        [Description("A standing rule the user stated")] string? constraint = null,
        string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var c = CycleState.Resolve(ctx, escalationId, decision, answer, constraint);
            var p = PipelineConfig.Load(ctx);
            return Compact.Render(new { ok = true, c.State, c.Rung, c.FailedRounds, c.Constraints, next = CycleState.Next(c, p) });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }
}
