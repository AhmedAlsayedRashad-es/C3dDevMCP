using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using C3dMCP.Engine;
using C3dMCP.Engine.Reload;
using C3dMCP.Sdk;
using Xunit;

namespace C3dMCP.Tests
{
    public class RunLifecycleTests
    {
        private static RunLifecycle NewRunning(Func<DateTime> clock = null)
        {
            var l = new RunLifecycle("0001", "PING", clock);
            l.MarkInvoking();
            l.MarkRunning();
            return l;
        }

        [Fact]
        public void RunReturned_without_defer_completes_by_host()
        {
            var l = NewRunning();
            l.RunReturned(null);
            Assert.Equal(RunState.Completed, l.State);
            Assert.Equal("host", l.By);
            Assert.True(l.IsTerminal);
        }

        [Fact]
        public void RunReturned_with_defer_enters_draining()
        {
            var l = NewRunning();
            Assert.True(l.Defer(5));
            l.RunReturned(null);
            Assert.Equal(RunState.Draining, l.State);
            Assert.Equal(5, l.QuietSeconds);
            Assert.False(l.IsTerminal);
        }

        [Fact]
        public void RunReturned_with_exception_fails()
        {
            var l = NewRunning();
            l.Defer(3);
            l.RunReturned(new InvalidOperationException("boom"));
            Assert.Equal(RunState.Failed, l.State);
            Assert.Contains("boom", l.Error);
        }

        [Fact]
        public void First_completion_wins_and_later_are_late()
        {
            var l = NewRunning();
            l.Defer(3);
            l.RunReturned(null);
            Assert.True(l.TryComplete("completed", "payload"));
            Assert.False(l.TryComplete("failed", "button"));
            Assert.Equal("payload", l.By);
            Assert.Equal(RunState.Completed, l.State);
        }

        [Fact]
        public void Payload_completing_inside_run_wins_over_host()
        {
            var l = NewRunning();
            Assert.True(l.TryComplete("custom-word", "payload"));
            l.RunReturned(null);
            Assert.Equal("custom-word", l.Status);
            Assert.Equal(RunState.Completed, l.State);
            Assert.Equal("payload", l.By);
        }

        [Fact]
        public void Inferred_completion_is_recorded()
        {
            var l = NewRunning();
            l.Defer(1);
            l.RunReturned(null);
            Assert.True(l.TryComplete("completed", "idle-ping", inferred: true));
            Assert.True(l.Inferred);
        }

        [Fact]
        public void Cannot_complete_before_running()
        {
            var l = new RunLifecycle("0002", "X");
            Assert.False(l.TryComplete("completed", "button"));
            l.MarkInvoking();
            Assert.False(l.TryComplete("completed", "button"));
        }

        [Fact]
        public void Abandoned_maps_to_abandoned_state()
        {
            var l = NewRunning();
            l.Defer(1);
            l.RunReturned(null);
            l.TryComplete(RunStatus.Abandoned, "unlock", reason: "stuck");
            Assert.Equal(RunState.Abandoned, l.State);
            Assert.Equal("stuck", l.Reason);
        }
    }

    public class IdlePingJudgeTests
    {
        private static IdleProbe P(DateTime at, double ms, long seq = 0, long cmd = 0, long db = 0, bool timedOut = false) =>
            new IdleProbe { AtUtc = at, ElapsedMs = ms, LogSeq = seq, CommandCount = cmd, DbChangeCount = db, TimedOut = timedOut };

        [Fact]
        public void Three_quiet_probes_over_ten_seconds_complete()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var j = new IdlePingJudge();
            Assert.Equal(IdleVerdict.Idle, j.Observe(P(t0, 40)));
            Assert.Equal(IdleVerdict.Idle, j.Observe(P(t0.AddSeconds(5), 35)));
            Assert.Equal(IdleVerdict.Complete, j.Observe(P(t0.AddSeconds(10), 30)));
            Assert.True(j.IsComplete);
        }

        [Fact]
        public void Three_quiet_probes_too_close_do_not_complete()
        {
            var t0 = DateTime.UtcNow;
            var j = new IdlePingJudge();
            j.Observe(P(t0, 40));
            j.Observe(P(t0.AddSeconds(2), 40));
            Assert.Equal(IdleVerdict.Idle, j.Observe(P(t0.AddSeconds(4), 40)));
            Assert.Equal(3, j.Streak);
            Assert.False(j.IsComplete);
            Assert.Equal(IdleVerdict.Complete, j.Observe(P(t0.AddSeconds(11), 40)));
        }

        [Fact]
        public void Activity_between_probes_resets_the_streak()
        {
            var t0 = DateTime.UtcNow;
            var j = new IdlePingJudge();
            j.Observe(P(t0, 40, seq: 10));
            j.Observe(P(t0.AddSeconds(5), 40, seq: 10));
            Assert.Equal(2, j.Streak);
            Assert.Equal(IdleVerdict.Idle, j.Observe(P(t0.AddSeconds(10), 40, seq: 11)));   // a log line landed
            Assert.Equal(1, j.Streak);
            Assert.False(j.IsComplete);
        }

        [Fact]
        public void Slow_sentinel_is_busy_and_timeout_is_main_thread_busy()
        {
            var t0 = DateTime.UtcNow;
            var j = new IdlePingJudge();
            j.Observe(P(t0, 40));
            Assert.Equal(IdleVerdict.Busy, j.Observe(P(t0.AddSeconds(5), 1840)));
            Assert.Equal(0, j.Streak);
            Assert.Equal(IdleVerdict.MainThreadBusy, j.Observe(P(t0.AddSeconds(10), 0, timedOut: true)));
        }

        [Fact]
        public void Db_changes_reset_even_when_log_is_quiet()
        {
            var t0 = DateTime.UtcNow;
            var j = new IdlePingJudge();
            j.Observe(P(t0, 40, db: 5));
            j.Observe(P(t0.AddSeconds(5), 40, db: 5));
            j.Observe(P(t0.AddSeconds(10), 40, db: 9));
            Assert.Equal(1, j.Streak);
        }
    }

    public class PortPlanTests
    {
        [Fact]
        public void Covers_the_whole_range_once_starting_at_pid_offset()
        {
            var ports = PortPlan.Candidates(pid: 18212).ToList();
            Assert.Equal(100, ports.Count);
            Assert.Equal(100, ports.Distinct().Count());
            Assert.Equal(48200 + 12, ports[0]);
            Assert.All(ports, p => Assert.InRange(p, 48200, 48299));
        }
    }

    public class JsonLineTests
    {
        [Fact]
        public void Escapes_and_omits_nulls()
        {
            var s = JsonLine.Object("a", "x\"y\n", "b", (string)null, "c", 1.5, "d", true,
                "e", new Dictionary<string, object> { ["k"] = new[] { 1, 2 } });
            Assert.Equal("{\"a\":\"x\\\"y\\n\",\"c\":1.5,\"d\":true,\"e\":{\"k\":[1,2]}}", s);
        }

        [Fact]
        public void Dates_are_iso_utc()
        {
            var s = JsonLine.Object("t", new DateTime(2026, 9, 9, 10, 21, 30, 1, DateTimeKind.Utc));
            Assert.Equal("{\"t\":\"2026-09-09T10:21:30.001Z\"}", s);
        }
    }

    public class RunLogAndContextTests
    {
        [Fact]
        public void Context_writes_one_stream_with_state_lines_and_late_marker()
        {
            var dir = Path.Combine(Path.GetTempPath(), "c3dmcp-tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "run.jsonl");
            var life = new RunLifecycle("0007", "IMPORT");
            using (var log = new RunLog(path))
            {
                var ctx = new EngineRunContext(life, log, new Dictionary<string, string> { ["file"] = "a.csv" });
                life.MarkInvoking();
                life.MarkRunning();
                ((IRunContext)ctx).Log("parsed 131 rows");
                ctx.Diagnostic("valveCount", false, new Dictionary<string, object> { ["expected"] = 12, ["actual"] = 9 });
                ctx.Feedback("create", "PressurePipeRun", "R-12", "RUN-12");
                ctx.Defer(3);
                life.RunReturned(null);
                Assert.Equal(RunState.Draining, life.State);
                ctx.Complete("completed", new Dictionary<string, object> { ["placed"] = 12 });
                ((IRunContext)ctx).Log("tail still talking");   // late
                ctx.Complete("failed");                          // late, ignored
            }
            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Contains("\"src\":\"host\"") && l.Contains("\"state\":\"Running\""));
            Assert.Contains(lines, l => l.Contains("\"src\":\"diag\"") && l.Contains("\"ok\":false"));
            Assert.Contains(lines, l => l.Contains("\"evt\":\"complete\"") && l.Contains("\"by\":\"payload\"") && l.Contains("\"placed\":12"));
            Assert.Contains(lines, l => l.Contains("\"late\":true") && l.Contains("tail still talking"));
            Assert.Contains(lines, l => l.Contains("\"evt\":\"complete-late\""));
            Assert.Equal(RunState.Completed, life.State);
            var seqs = lines.Select(l => int.Parse(l.Substring(7, l.IndexOf(',') - 7))).ToList();
            Assert.Equal(Enumerable.Range(1, lines.Length), seqs);

            var doc = RunDocument.Render(life, new Dictionary<string, object> { ["taskId"] = "0003" }, seqs.Last());
            Assert.Contains("\"state\":\"completed\"", doc);
            Assert.Contains("\"taskId\":\"0003\"", doc);
            Directory.Delete(dir, true);
        }

        [Fact]
        public void Log_is_readable_while_open()
        {
            var path = Path.Combine(Path.GetTempPath(), "c3dmcp-tests", Guid.NewGuid().ToString("N") + ".jsonl");
            using (var log = new RunLog(path))
            {
                log.Append("log", "msg", "hello");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                    Assert.Contains("hello", r.ReadToEnd());
            }
            File.Delete(path);
        }
    }

    public class ReloadEngineTests
    {
        private sealed class Svc : IC3dPatchService
        {
            public List<string> Calls = new List<string>();
            public ReloadState Capture(object app) { Calls.Add("capture"); return new ReloadState { CapturedAt = DateTime.UtcNow }; }
            public void SafeEnd(object app, object ctx, ReloadState s) { Calls.Add("safeEnd"); }
            public void Refresh(object app, object ctx, ReloadState s) { Calls.Add("refresh"); }
        }

        [Fact]
        public void Refresh_runs_capture_safeend_refresh_and_swaps_generation()
        {
            var s1 = new Svc(); var s2 = new Svc();
            var g1 = new ExecutorGeneration(new object(), new object(), s1, "Addin", "Addin_r1");
            var g2 = new ExecutorGeneration(new object(), new object(), s2, "Addin", "Addin_r2");
            var life = StartupGenerationActivator.Activate(g1, _ => true);
            life.BeginRefresh(g2);
            life.ExecutePending(new object());
            Assert.Same(g2, life.RequireRunningGeneration());
            Assert.Equal(new[] { "capture", "safeEnd" }, s1.Calls);
            Assert.Equal(new[] { "refresh" }, s2.Calls);
            Assert.False(life.HasPending);
        }

        [Fact]
        public void Versioned_identity_and_bridge_set()
        {
            Assert.Equal("r5", VersionedIdentity.ValidateAndExtractVersion("MyAddin_r5", "MyAddin"));
            Assert.Throws<InvalidDataException>(() => VersionedIdentity.ValidateAndExtractVersion("MyAddin", "MyAddin"));
            Assert.True(BridgeAssemblyResolver.IsBridgeAssembly("c3dmcp.sdk"));
            Assert.True(BridgeAssemblyResolver.IsBridgeAssembly("C3dMCP.Engine"));
            Assert.False(BridgeAssemblyResolver.IsBridgeAssembly("Civil3DInfoWorksAddin"));
        }
    }
}
