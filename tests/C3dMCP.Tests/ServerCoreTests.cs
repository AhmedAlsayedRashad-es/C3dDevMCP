using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using C3dMCP.Server.Core;
using Xunit;

namespace C3dMCP.Tests
{
    public sealed class TempProject : IDisposable
    {
        public string Dir { get; }
        public ProjectContext Ctx { get; }
        public TempProject()
        {
            Dir = Path.Combine(Path.GetTempPath(), "c3dmcp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "Demo.csproj"), "<Project><PropertyGroup><RootNamespace>Demo</RootNamespace></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Dir, "c3d.json"), "{ \"project\": \"" + "t-" + Path.GetFileName(Dir) + "\", \"config\": \"R2024\" }");
            Ctx = ProjectStore.Load(Path.Combine(Dir, "c3d.json"));
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
            try { Directory.Delete(Ctx.StoreDir, true); } catch { }
        }
    }

    public class LadderTests
    {
        [Fact]
        public void Two_failed_rounds_climb_then_escalate_and_resolve_clears()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "valve at bend", new[] { "a" }, null);
            CycleState.OnNewTask(ctx, ProjectStore.ActiveTask(ctx)!);
            CycleState.Apply(ctx, new CycleState.Event { Kind = "problem", Problem = "valve at bend" });
            var c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail", Reason = "r1" });
            Assert.Equal(2, c.Rung); Assert.Equal(1, c.FailedRounds); Assert.Equal("planning", c.State);
            c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail", Reason = "r2" });
            Assert.Equal("escalated", c.State); Assert.NotNull(c.EscalationOpen); Assert.Equal(2, c.EscalationOpen!.Rounds.Count);
            var p = PipelineConfig.Load(ctx);
            var next = CycleState.Next(c, p);
            Assert.Contains("escalation", JsonSerializer.Serialize(next));
            c = CycleState.Resolve(ctx, c.EscalationOpen.Id, "retry", "try the other clamp", "never touch tools/");
            Assert.Equal("planning", c.State); Assert.Null(c.EscalationOpen); Assert.Contains("never touch tools/", c.Constraints);
            Assert.Equal(1, c.FailedRounds);   // one more round before the user again
        }

        [Fact]
        public void Partial_with_progress_resets_to_start_rung_and_pass_finishes()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "t", new[] { "a" }, null);
            CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail" });
            var c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "partial", Progress = true });
            Assert.Equal(1, c.Rung); Assert.Equal(0, c.FailedRounds); Assert.Equal("planning", c.State);
            c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "pass" });
            Assert.Equal("done", c.State);
        }

        [Fact]
        public void Climb_from_escalation_marks_rung_approved_and_rung3_needs_human_otherwise()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "t", new[] { "a" }, null);
            CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail" });
            var c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail" });
            c = CycleState.Resolve(ctx, c.EscalationOpen!.Id, "climb", null, null);
            Assert.Equal(3, c.Rung); Assert.True(c.HumanApproved[3]);
            var p = PipelineConfig.Load(ctx);
            var next = JsonSerializer.Serialize(CycleState.Next(c, p));
            Assert.Contains("diagnostician", next);
        }

        [Fact]
        public void Step_done_advances_and_allowed_agents_follow_the_pipeline()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "t", new[] { "a" }, null);
            var p = PipelineConfig.Load(ctx);
            var c = CycleState.Load(ctx);
            var allowed = CycleState.AllowedAgents(c, p);
            Assert.Contains("c3d-planner", allowed); Assert.DoesNotContain("c3d-implementer", allowed);
            c = CycleState.Apply(ctx, new CycleState.Event { Kind = "step-done", Agent = "c3d-planner" });
            allowed = CycleState.AllowedAgents(c, p);
            Assert.Contains("c3d-implementer", allowed); Assert.DoesNotContain("c3d-planner", allowed);
        }
    }

    public class HookDeciderTests
    {
        private static JsonNode Agent(string sub, string? model = null) =>
            JsonNode.Parse("{\"tool_name\":\"Agent\",\"tool_input\":{\"subagent_type\":\"" + sub + "\"" + (model != null ? ",\"model\":\"" + model + "\"" : "") + "}}")!;

        [Fact]
        public void Blocks_wrong_step_escalation_and_model_mismatch()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "t", new[] { "a" }, null);
            CycleState.OnNewTask(ctx, ProjectStore.ActiveTask(ctx)!);
            var p = PipelineConfig.Load(ctx);
            var c = CycleState.Load(ctx);
            Assert.False(HookDecider.PreAgent(Agent("c3d-planner"), c, p).Block);
            Assert.False(HookDecider.PreAgent(Agent("Explore"), c, p).Block);
            var d = HookDecider.PreAgent(Agent("c3d-planner-max"), c, p);
            Assert.True(d.Block); Assert.Contains("rung 1", d.Reason);
            d = HookDecider.PreAgent(Agent("c3d-planner", "sonnet"), c, p);
            Assert.True(d.Block); Assert.Contains("model", d.Reason);
            CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail" });
            c = CycleState.Apply(ctx, new CycleState.Event { Kind = "verdict", Verdict = "fail" });
            d = HookDecider.PreAgent(Agent("c3d-planner"), c, p);
            Assert.True(d.Block); Assert.Contains("c3d_escalation_resolve", d.Reason);
        }

        [Fact]
        public void Implementer_blocked_without_verdict_for_this_cycles_run()
        {
            using var tp = new TempProject();
            var ctx = tp.Ctx;
            ProjectStore.NewTask(ctx, "t", new[] { "a" }, null);
            CycleState.OnNewTask(ctx, ProjectStore.ActiveTask(ctx)!);
            CycleState.Apply(ctx, new CycleState.Event { Kind = "step-done", Agent = "c3d-planner" });
            CycleState.OnRunStarted(ctx, ProjectStore.ActiveTask(ctx)!, "0001", "X");
            var c = CycleState.Apply(ctx, new CycleState.Event { Kind = "step-done", Agent = "c3d-implementer" });
            Assert.Equal("analyzing", c.State);
            var p = PipelineConfig.Load(ctx);
            var d = HookDecider.PreAgent(Agent("c3d-implementer"), c, p);
            Assert.True(d.Block); Assert.Contains("verdict", d.Reason);
            Assert.False(HookDecider.PreAgent(Agent("c3d-analyzer"), c, p).Block);
            var stop = HookDecider.Stop(JsonNode.Parse("{}")!, c, null);
            Assert.True(stop.Block);
            Assert.False(HookDecider.Stop(JsonNode.Parse("{\"stop_hook_active\":true}")!, c, null).Block);
        }

        [Fact]
        public void PreTool_blocks_run_while_instance_has_live_run_and_unlock_without_reason()
        {
            var live = new InstanceInfo { Port = 48200, ActiveRun = JsonDocument.Parse("{\"runId\":\"0007\",\"state\":\"draining\"}").RootElement };
            var d = HookDecider.PreTool(JsonNode.Parse("{\"tool_name\":\"mcp__c3dmcp__c3d_run\",\"tool_input\":{\"command\":\"X\"}}")!, new Cycle(), new List<InstanceInfo> { live }, null);
            Assert.True(d.Block); Assert.Contains("0007", d.Reason);
            d = HookDecider.PreTool(JsonNode.Parse("{\"tool_name\":\"mcp__c3dmcp__c3d_unlock\",\"tool_input\":{}}")!, new Cycle(), new List<InstanceInfo>(), null);
            Assert.True(d.Block);
            d = HookDecider.PreTool(JsonNode.Parse("{\"tool_name\":\"mcp__c3dmcp__c3d_log_query\",\"tool_input\":{}}")!, new Cycle(), new List<InstanceInfo> { live }, null);
            Assert.False(d.Block);
        }

        [Fact]
        public void PreMessage_blocks_redirects_to_c3d_agents()
        {
            Assert.True(HookDecider.PreMessage(JsonNode.Parse("{\"tool_input\":{\"to\":\"c3d-implementer\"}}")!, Array.Empty<string>()).Block);
            Assert.False(HookDecider.PreMessage(JsonNode.Parse("{\"tool_input\":{\"to\":\"someone\"}}")!, Array.Empty<string>()).Block);
        }
    }

    public class CompactAndLogTests
    {
        [Fact]
        public void Compact_caps_size_and_marks_more()
        {
            var big = new { lines = Enumerable.Range(0, 500).Select(i => new string('x', 300) + i).ToList() };
            var s = Compact.Render(big);
            Assert.True(s.Length <= Compact.MaxBytes + 64, "len=" + s.Length);
            Assert.Contains("\"more\":true", s);
        }

        [Fact]
        public void LogQuery_tail_grep_src_since()
        {
            var path = Path.Combine(Path.GetTempPath(), "c3dmcp-tests", Guid.NewGuid().ToString("N") + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>();
            for (int i = 1; i <= 50; i++)
                lines.Add("{\"seq\":" + i + ",\"src\":\"" + (i % 5 == 0 ? "diag" : "log") + "\",\"msg\":\"line " + i + "\"" + (i % 10 == 0 ? ",\"ok\":false" : "") + "}");
            File.WriteAllLines(path, lines);
            var r = LogQuery.Query(path, tail: 3, grep: null, sinceSeq: null, src: null, max: 40);
            Assert.Equal(3, r.Lines.Count); Assert.Equal(50, r.LastSeq); Assert.Equal(50, r.Total);
            r = LogQuery.Query(path, null, "ok\\\":false", null, "diag", 40);
            Assert.Equal(5, r.Matched);
            r = LogQuery.Query(path, null, null, 45, null, 40);
            Assert.Equal(5, r.Lines.Count);
            r = LogQuery.Query(path, null, null, null, null, 10);
            Assert.True(r.Truncated); Assert.Equal(10, r.Next);
            var counts = LogQuery.Counts(path);
            Assert.Equal(5, counts["diagFailed"]); Assert.Equal(10, counts["diag"]);
            File.Delete(path);
        }
    }
}
