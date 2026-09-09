using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using C3dMCP.Server.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class RunTools
{
    [McpServerTool(Name = "c3d_run"), Description("One iteration: reset the drawing to the baseline (or take the first baseline), build the payload, hot-reload it into the instance, invoke the command, and wait briefly for Run() to return. Returns the run id and state. State 'draining' means the command has an async tail: call c3d_wait_run. Refused with run-in-flight while another run is live, and with no-task when no task is active.")]
    public static string Run(
        [Description("Payload command name, e.g. IMPORTIRRG or PING")] string command,
        [Description("Port of the Civil3D instance. Optional when exactly one instance runs.")] int? instance = null,
        [Description("Command arguments as key/value strings.")] Dictionary<string, string>? args = null,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null,
        [Description("Task id; defaults to the active task.")] string? taskId = null,
        [Description("Keep the drawing as it is (no reset before the run).")] bool keepState = false,
        [Description("Build the payload before reloading (default true).")] bool build = true,
        [Description("Reload the payload before invoking (default true).")] bool reload = true,
        [Description("Seconds to wait for Run() to return before answering (default 60).")] int waitSec = 60,
        [Description("Seconds a draining tail may take before the host fails it (default 1800).")] int maxDrainSeconds = 1800)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var inst = InstanceDiscovery.Resolve(instance);
            if (!string.Equals(inst.Version, ctx.Version, StringComparison.Ordinal))
                throw new ToolError("version-mismatch", $"project config {ctx.Config} targets Civil3D {ctx.Version} but instance {inst.Port} is Civil3D {inst.Version}");
            using var c = new HostClient(inst);

            var st = c.Status();
            var active = st["activeRun"];
            if (active is JsonObject ao)
                throw new ToolError("run-in-flight", $"run {ao["runId"]} is {ao["state"]} on instance {inst.Port}; wait with c3d_wait_run or c3d_unlock with a reason", "run",
                    new { runId = ao["runId"]?.ToString(), state = ao["state"]?.ToString() });

            taskId ??= ProjectStore.ActiveTask(ctx) ?? throw new ToolError("no-task", "no active task; call c3d_task_new with the expectations first", "run");
            var task = ProjectStore.LoadTask(ctx, taskId) ?? throw new ToolError("no-task", "task " + taskId + " does not exist", "run");

            // 1. reset-or-baseline
            string prep;
            if (keepState) prep = "kept";
            else if (st["baseline"]?["valid"]?.GetValue<bool>() == true) { c.Reset(false); prep = "reset"; }
            else { c.Baseline(); prep = "baseline"; }

            // 2. build
            PayloadBuilder.Result? b = null;
            string? dll = null;
            if (build)
            {
                b = PayloadBuilder.Build(ctx.Csproj, ctx.Config);
                if (!b.Ok) throw new ToolError("build-failed", "dotnet build failed", "build", new { errors = b.Errors, tail = b.Tail, seconds = b.Seconds });
                dll = b.DeployedDll;
            }
            else dll = LatestDeployed(ctx);

            // 3. reload
            string? loaded = st["payload"]?["loadedVersion"]?.GetValue<string>();
            if (reload)
            {
                if (dll == null) throw new ToolError("no-dll", "no deployed payload found; build first", "reload");
                var rl = c.Reload(dll, ctx.AddinName);
                loaded = rl["loadedVersion"]?.GetValue<string>();
            }

            // 4. invoke
            var runId = ProjectStore.AllocateRun(ctx, taskId);
            var runDir = ProjectStore.RunDir(ctx, taskId, runId);
            c.StartRun(runId, runDir, command, args, taskId, maxDrainSeconds);
            task.LatestRun = runId; task.LatestVerdict = null; ProjectStore.SaveTask(ctx, task);
            CycleState.OnRunStarted(ctx, taskId, runId, command);

            // 5. wait for Run() to return (not for completion)
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, waitSec));
            JsonNode w = c.Wait(runId, 1, 0);
            long seq = 0;
            while (!IsTerminal(w) && State(w) is "invoking" or "running" && DateTime.UtcNow < deadline)
            {
                seq = w["seq"]?.GetValue<long>() ?? seq;
                w = c.Wait(runId, 10, seq);
            }
            var wait = Summarize(w, runDir);
            return Compact.Render(new
            {
                ok = true, runId, taskId, command, stage = "run", prep, built = b == null ? null : new { rev = b.Rev, seconds = b.Seconds }, loaded,
                state = wait.State, status = wait.Status, by = wait.By, inferred = wait.Inferred, terminal = wait.Terminal, seq = wait.Seq,
                tail = wait.Tail, hint = wait.Hint,
            });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_wait_run"), Description("Block until the run is terminal (completed / failed / abandoned) or the timeout passes. Reports progress. On timeout returns timedOut=true with seq, so call again with sinceSeq. A completion with inferred=true came from the idle ping, not from the payload.")]
    public static string WaitRun(
        [Description("Run id from c3d_run")] string runId,
        [Description("Port of the Civil3D instance. Optional when exactly one instance runs.")] int? instance = null,
        [Description("Seconds to wait, max 600 (default 120).")] int timeoutSec = 120,
        [Description("Only report lines after this seq (from a previous answer).")] long sinceSeq = 0,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        try
        {
            var inst = InstanceDiscovery.Resolve(instance);
            using var c = new HostClient(inst);
            timeoutSec = Math.Max(1, Math.Min(600, timeoutSec));
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            long seq = sinceSeq;
            JsonNode w = c.Wait(runId, 1, seq);
            var t0 = DateTime.UtcNow;
            while (!IsTerminal(w) && DateTime.UtcNow < deadline)
            {
                seq = w["seq"]?.GetValue<long>() ?? seq;
                progress?.Report(new ProgressNotificationValue { Progress = (float)(DateTime.UtcNow - t0).TotalSeconds, Total = timeoutSec, Message = $"run {runId} {State(w)} seq={seq}" + IdleText(w) });
                int slice = (int)Math.Min(25, Math.Max(1, (deadline - DateTime.UtcNow).TotalSeconds));
                w = c.Wait(runId, slice, seq);
            }
            string? runDir = null;
            try { runDir = ProjectStore.FindRunDir(ProjectStore.Resolve(projectDir), null, runId); } catch { }
            var s = Summarize(w, runDir);
            return Compact.Render(new
            {
                ok = true, runId, state = s.State, status = s.Status, by = s.By, inferred = s.Inferred, terminal = s.Terminal,
                timedOut = !s.Terminal, seq = s.Seq, elapsedMs = w["elapsedMs"]?.GetValue<long>(), idlePing = w["idlePing"], counts = s.Counts, error = w["error"]?.GetValue<string>(),
                tail = s.Tail, hint = s.Hint,
            });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_log_query"), Description("Read a slice of a run's unified log (log / diag / note / feedback / host lines) from disk. Use tail for the end, grep (regex) to search, src to filter one source, sinceSeq to continue. Never returns the whole file; the answer is capped.")]
    public static string LogQuery(
        [Description("Run id")] string runId,
        [Description("Last N matching lines")] int? tail = null,
        [Description("Regex, case-insensitive, matched against the raw JSON line")] string? grep = null,
        [Description("Only lines with seq greater than this")] long? sinceSeq = null,
        [Description("One of: log, diag, note, feedback, host")] string? src = null,
        [Description("Max lines (default 40, max 200)")] int max = 40,
        [Description("Task id when the run id is not unique across tasks")] string? taskId = null,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var dir = ProjectStore.FindRunDir(ctx, taskId, runId) ?? throw new ToolError("no-run", "run " + runId + " not found in project " + ctx.Name);
            var r = Core.LogQuery.Query(Path.Combine(dir, "run.jsonl"), tail, grep, sinceSeq, src, max);
            return Compact.Render(new { ok = true, runId, total = r.Total, matched = r.Matched, lastSeq = r.LastSeq, truncated = r.Truncated, next = r.Next, lines = r.Lines });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_baseline"), Description("Mark the current drawing state as the baseline every later run is reset to. Refused while a run is live.")]
    public static string Baseline(int? instance = null)
    {
        try { var inst = InstanceDiscovery.Resolve(instance); using var c = new HostClient(inst); return Compact.Render(new { ok = true, result = c.Baseline() }); }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_reset"), Description("Rewind the drawing to the baseline (soft: UNDO back to the mark; hard: reopen the drawing file). Refused while a run is live, and soft is refused without a baseline.")]
    public static string Reset(int? instance = null, [Description("Reopen the drawing from disk instead of UNDO")] bool hard = false)
    {
        try { var inst = InstanceDiscovery.Resolve(instance); using var c = new HostClient(inst); return Compact.Render(new { ok = true, result = c.Reset(hard) }); }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_unlock"), Description("Force-end a run that is stuck draining (or running) so the instance accepts new runs. The run is recorded as abandoned by unlock with your reason. Use only after c3d_wait_run showed no progress and the palette shows nothing moving.")]
    public static string Unlock([Description("Why you are abandoning the run")] string reason, [Description("Run id; defaults to the active run")] string? runId = null, int? instance = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ToolError("reason-required", "give a reason for abandoning the run");
            var inst = InstanceDiscovery.Resolve(instance);
            using var c = new HostClient(inst);
            runId ??= c.Status()["activeRun"]?["runId"]?.GetValue<string>() ?? throw new ToolError("no-active-run", "no run is live on instance " + inst.Port);
            return Compact.Render(new { ok = true, result = c.Complete(runId, "abandoned", "unlock", reason) });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_recover"), Description("Capture a screenshot of the Civil3D window and its palette to a file, report whether the main thread answers, and optionally send ESC (only when Civil3D is the foreground window and no run is draining).")]
    public static string Recover(int? instance = null, [Description("Send two ESC keystrokes to Civil3D")] bool sendEsc = false)
    {
        try
        {
            var inst = InstanceDiscovery.Resolve(instance);
            using var c = new HostClient(inst);
            var st = c.Status();
            bool draining = st["activeRun"]?["state"]?.GetValue<string>() == "draining";
            var shots = Recovery.Screenshot(inst.Pid);
            bool esc = false;
            if (sendEsc && !draining) esc = Recovery.SendEscIfForeground(inst.Pid);
            return Compact.Render(new { ok = true, screenshots = shots, mainThreadResponsive = st["mainThreadResponsive"], mainThreadProbeMs = st["mainThreadProbeMs"], escSent = esc, refusedEsc = sendEsc && draining ? "a run is draining" : null, activeRun = st["activeRun"], hostLog = c.HostLog(5)["lines"] });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string? LatestDeployed(ProjectContext ctx)
    {
        var dir = Path.Combine(Paths.Payloads, ctx.AddinName, ctx.Version);
        if (!Directory.Exists(dir)) return null;
        var latest = Directory.GetDirectories(dir).Select(d => int.TryParse(Path.GetFileName(d), out var n) ? n : -1).Where(n => n >= 0).DefaultIfEmpty(-1).Max();
        if (latest < 0) return null;
        var dll = Path.Combine(dir, latest.ToString(), ctx.AddinName + ".dll");
        return File.Exists(dll) ? dll : null;
    }

    private static bool IsTerminal(JsonNode w) => w["terminal"]?.GetValue<bool>() == true;
    private static string State(JsonNode w) => w["state"]?.GetValue<string>() ?? "";
    private static string IdleText(JsonNode w)
    {
        var ip = w["idlePing"];
        return ip == null ? "" : $" idle {ip["streak"]}/{ip["required"]}";
    }

    private sealed record WaitSummary(string State, string? Status, string? By, bool Inferred, bool Terminal, long Seq, List<string> Tail, Dictionary<string, long>? Counts, string? Hint);

    private static WaitSummary Summarize(JsonNode w, string? runDir)
    {
        var state = State(w);
        var tail = new List<string>();
        if (w["newLines"] is JsonArray arr)
            foreach (var l in arr.TakeLast(5)) tail.Add(OneLine(l));
        Dictionary<string, long>? counts = null;
        if (runDir != null) { try { counts = Core.LogQuery.Counts(Path.Combine(runDir, "run.jsonl")); } catch { } }
        bool terminal = IsTerminal(w);
        string? hint = terminal
            ? (counts != null && counts["diagFailed"] > 0 ? $"{counts["diagFailed"]} diagnostics failed: c3d_log_query src=diag grep=\"ok\\\":false\"" : "read the result with c3d_log_query tail=20 or src=diag")
            : state == "draining" ? "the command has an async tail; call c3d_wait_run until terminal" : "call c3d_wait_run";
        return new WaitSummary(state, w["status"]?.GetValue<string>(), w["by"]?.GetValue<string>(), w["inferred"]?.GetValue<bool>() == true, terminal, w["seq"]?.GetValue<long>() ?? 0, tail, counts, hint);
    }

    private static string OneLine(JsonNode? l)
    {
        if (l == null) return "";
        var src = l["src"]?.GetValue<string>() ?? "";
        switch (src)
        {
            case "log": return "[log] " + l["msg"];
            case "note": return "[note] " + l["msg"] + (l["data"] != null ? " " + l["data"]!.ToJsonString() : "");
            case "diag": return "[diag] " + l["name"] + " ok=" + l["ok"] + (l["data"] != null ? " " + l["data"]!.ToJsonString() : "");
            case "feedback": return "[feedback] " + l["op"] + " " + l["type"] + " " + l["handle"];
            case "host": return "[host] " + (l["evt"]?.ToString() == "state" ? "state " + l["state"] : l["evt"]?.ToString() == "complete" ? "complete " + l["status"] + " by " + l["by"] : l["evt"]?.ToString() == "idle-ping" ? "idle-ping " + l["ms"] + "ms " + l["verdict"] : l.ToJsonString());
            default: return l.ToJsonString();
        }
    }
}
