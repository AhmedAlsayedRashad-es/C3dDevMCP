using System;
using System.Collections.Generic;
using System.Text.Json;
using C3dMCP.Engine;

namespace C3dMCP.Host
{
    /// <summary>The HTTP routes between the MCP server and this instance.</summary>
    internal static class Routes
    {
        private sealed class ReloadBody { public string Dll { get; set; } public string AddinName { get; set; } }
        private sealed class CompleteBody { public string Status { get; set; } public string By { get; set; } public string Reason { get; set; } }
        private sealed class ResetBody { public bool Hard { get; set; } }

        internal static void Register(HostApp app)
        {
            var api = app.Api;
            var st = app.State;

            api.Map("GET", "/v1/status", _ =>
            {
                double probeMs;
                bool responsive = RealCivil3DHost.ProbeMainThread(1500, out probeMs);
                var active = app.Runs.Active;
                return new
                {
                    pid = st.Pid,
                    version = st.CivilVersion,
                    hostVersion = st.HostVersion,
                    port = st.Port,
                    listener = st.ListenerState,
                    drawing = st.Drawing,
                    started = st.StartedUtc,
                    payload = st.PayloadName == null ? null : new { name = st.PayloadName, loadedVersion = st.PayloadVersion, loadedAt = st.PayloadLoadedUtc, lastError = st.LastReloadError },
                    activeRun = active == null || active.Life.IsTerminal ? null : new { runId = active.RunId, state = active.Life.State.ToString().ToLowerInvariant(), since = st.ActiveRunSinceUtc },
                    baseline = new { valid = st.BaselineValid, markedAt = st.BaselineMarkedUtc },
                    mainThreadResponsive = responsive,
                    mainThreadProbeMs = Math.Round(probeMs, 1),
                    sentinel = new { verified = st.SentinelVerified, roundTripMs = st.SentinelRoundTripMs },
                };
            });

            api.Map("GET", "/v1/log", r =>
            {
                int tail; int.TryParse(r.Query("tail", "50"), out tail);
                return new { lines = HostLog.Tail(Math.Max(1, Math.Min(200, tail))) };
            });

            api.Map("POST", "/v1/reload", r =>
            {
                if (app.Runs.HasNonTerminalRun)
                    throw new HttpError(409, "run-in-flight", "run " + app.Runs.Active.RunId + " is " + app.Runs.Active.Life.State.ToString().ToLowerInvariant());
                var b = r.Json<ReloadBody>() ?? new ReloadBody();
                var res = app.Reload.Reload(b.Dll, b.AddinName);
                if (!res.Ok) return HttpApi.Status(422, new { ok = false, error = res.Error, code = res.Code, ms = Math.Round(res.Ms) });
                return new { ok = true, loadedVersion = res.LoadedVersion, addinName = res.AddinName, ms = Math.Round(res.Ms) };
            });

            api.Map("POST", "/v1/runs", r =>
            {
                var req = r.Json<RunManager.StartRequest>() ?? throw new HttpError(400, "bad-request", "body required");
                var rec = app.Runs.Start(req);
                return HttpApi.Status(202, new { runId = rec.RunId, state = rec.Life.State.ToString().ToLowerInvariant() });
            });

            api.Map("GET", "/v1/runs", _ =>
            {
                var list = new List<object>();
                foreach (var rec in app.Runs.Recent())
                    list.Add(Summary(rec));
                return new { runs = list };
            });

            api.Map("GET", "/v1/runs/([^/]+)", r =>
            {
                var rec = Require(app, r.Route.Groups[1].Value);
                return Summary(rec, full: true);
            });

            api.Map("GET", "/v1/runs/([^/]+)/wait", r =>
            {
                var rec = Require(app, r.Route.Groups[1].Value);
                int timeoutSec; int.TryParse(r.Query("timeoutSec", "25"), out timeoutSec);
                long sinceSeq; long.TryParse(r.Query("sinceSeq", "0"), out sinceSeq);
                timeoutSec = Math.Max(0, Math.Min(25, timeoutSec));
                app.Runs.Wait(rec, timeoutSec * 1000, sinceSeq);
                var lines = app.Runs.LinesSince(rec, sinceSeq, 20);
                var life = rec.Life;
                return new
                {
                    runId = rec.RunId,
                    state = life.State.ToString().ToLowerInvariant(),
                    status = life.Status,
                    by = life.By,
                    inferred = life.Inferred,
                    terminal = life.IsTerminal,
                    seq = rec.Log.Seq,
                    elapsedMs = (long)((life.EndedUtc ?? DateTime.UtcNow) - life.CreatedUtc).TotalMilliseconds,
                    newLines = lines.ConvertAll(l => JsonSerializer.Deserialize<JsonElement>(l)),
                    idlePing = rec.Pinger?.Summary(),
                    error = life.Error,
                };
            });

            api.Map("POST", "/v1/runs/([^/]+)/complete", r =>
            {
                var rec = Require(app, r.Route.Groups[1].Value);
                var b = r.Json<CompleteBody>() ?? new CompleteBody();
                var previous = rec.Life.State;
                if (rec.Life.IsTerminal) throw new HttpError(409, "already-terminal", "run " + rec.RunId + " is " + previous.ToString().ToLowerInvariant());
                if (previous != RunState.Draining && b.By != "unlock")
                    throw new HttpError(409, "not-draining", "run " + rec.RunId + " is " + previous.ToString().ToLowerInvariant() + "; only a draining run can be completed by hand (use unlock to abandon a running one)");
                bool ok = app.Runs.Complete(rec, string.IsNullOrWhiteSpace(b.Status) ? (b.By == "unlock" ? "abandoned" : "completed") : b.Status, b.By ?? "mcp", b.Reason);
                return new { ok, previousState = previous.ToString().ToLowerInvariant(), state = rec.Life.State.ToString().ToLowerInvariant() };
            });

            api.Map("POST", "/v1/idle-ping", _ =>
            {
                var rec = app.Runs.Active;
                if (rec == null || rec.Life.State != RunState.Draining) throw new HttpError(409, "not-draining", "no draining run");
                if (rec.Pinger == null) throw new HttpError(409, "no-pinger", "idle pinger not running");
                rec.Pinger.ProbeNow();
                return new { ok = true, idlePing = rec.Pinger.Summary() };
            });

            api.Map("POST", "/v1/baseline", _ =>
            {
                if (app.Runs.HasNonTerminalRun) throw new HttpError(409, "run-in-flight", "a run is live");
                app.Civil.Baseline();
                st.BaselineValid = true; st.BaselineMarkedUtc = DateTime.UtcNow;
                app.Registry?.Write(); app.PaletteHost?.Refresh();
                HostLog.Write("baseline marked");
                return new { ok = true, markedAt = st.BaselineMarkedUtc };
            });

            api.Map("POST", "/v1/reset", r =>
            {
                if (app.Runs.HasNonTerminalRun) throw new HttpError(409, "run-in-flight", "a run is live");
                var b = r.Json<ResetBody>() ?? new ResetBody();
                if (!b.Hard && !st.BaselineValid) throw new HttpError(412, "no-baseline", "no baseline mark in this session; call /v1/baseline first");
                app.Civil.Reset(b.Hard);
                HostLog.Write("reset " + (b.Hard ? "hard" : "soft"));
                return new { ok = true, hard = b.Hard };
            });
        }

        private static RunManager.RunRecord Require(HostApp app, string runId) =>
            app.Runs.Find(runId) ?? throw new HttpError(404, "run-not-found", "run " + runId + " is not in this instance");

        private static object Summary(RunManager.RunRecord rec, bool full = false)
        {
            var l = rec.Life;
            return new
            {
                runId = rec.RunId, command = rec.Command, taskId = rec.TaskId, runDir = full ? rec.RunDir : null,
                state = l.State.ToString().ToLowerInvariant(), status = l.Status, by = l.By, inferred = l.Inferred ? (bool?)true : null,
                deferred = l.Deferred, createdAt = l.CreatedUtc, startedAt = l.StartedUtc, endedAt = l.EndedUtc,
                seq = rec.Log.Seq, error = l.Error, idlePing = rec.Pinger?.Summary(),
            };
        }
    }
}
