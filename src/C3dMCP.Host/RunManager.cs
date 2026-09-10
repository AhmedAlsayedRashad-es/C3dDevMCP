using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using C3dMCP.Engine;
using C3dMCP.Sdk;

namespace C3dMCP.Host
{
    /// <summary>One run at a time. Binds RunLifecycle + RunLog + EngineRunContext, dispatches the
    /// payload command on the main thread, keeps the last 30 runs and a ring of lines for the
    /// palette, writes run.json on every transition, and serves long-poll waits.</summary>
    public sealed class RunManager
    {
        public const int MaxRecent = 30;
        public const int RingSize = 5000;

        public sealed class StartRequest
        {
            public string RunId { get; set; }
            public string RunDir { get; set; }
            public string Command { get; set; }
            public Dictionary<string, string> Args { get; set; }
            public string TaskId { get; set; }
            public int MaxDrainSeconds { get; set; } = 1800;
        }

        /// <summary>A run as the palette and the API see it. Holds only values.</summary>
        public sealed class RunRecord
        {
            public string RunId, Command, TaskId, RunDir;
            public RunLifecycle Life;
            public RunLog Log;
            public EngineRunContext Ctx;
            public readonly List<string> Lines = new List<string>();   // ring of JSON lines
            public long FirstSeqInRing = 1;
            public int MaxDrainSeconds;
            public Timer DrainTimer;
            public IdlePinger Pinger;
            public CommandRecorder Commands;
            public DateTime? DrainingSinceUtc;
        }

        private readonly HostApp _app;
        private readonly object _sync = new object();
        private readonly LinkedList<RunRecord> _recent = new LinkedList<RunRecord>();
        private RunRecord _active;

        public RunManager(HostApp app) { _app = app; }

        public event Action<RunRecord> Changed;

        public RunRecord Active { get { lock (_sync) { return _active; } } }

        public RunRecord Find(string runId)
        {
            lock (_sync)
            {
                foreach (var r in _recent) if (r.RunId == runId) return r;
                return null;
            }
        }

        public List<RunRecord> Recent()
        {
            lock (_sync) { return new List<RunRecord>(_recent); }
        }

        public bool HasNonTerminalRun
        {
            get { var a = Active; return a != null && !a.Life.IsTerminal; }
        }

        public RunRecord Start(StartRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.RunId)) throw new HttpError(400, "bad-request", "runId is required");
            if (string.IsNullOrWhiteSpace(req.RunDir)) throw new HttpError(400, "bad-request", "runDir is required");
            if (string.IsNullOrWhiteSpace(req.Command)) throw new HttpError(400, "bad-request", "command is required");

            var executor = _app.Reload.CurrentExecutor;
            if (executor == null) throw new HttpError(409, "no-payload", "No payload is loaded; reload first.");

            RunRecord rec;
            lock (_sync)
            {
                if (_active != null && !_active.Life.IsTerminal)
                    throw new HttpError(409, "run-in-flight", "run " + _active.RunId + " is " + _active.Life.State.ToString().ToLowerInvariant() + "; wait, complete or unlock it first");
                if (Find(req.RunId) != null)
                    throw new HttpError(409, "run-id-used", "run " + req.RunId + " already exists in this instance");

                Directory.CreateDirectory(req.RunDir);
                var life = new RunLifecycle(req.RunId, req.Command);
                var log = new RunLog(Path.Combine(req.RunDir, "run.jsonl"));
                var ctx = new EngineRunContext(life, log, req.Args ?? new Dictionary<string, string>());
                rec = new RunRecord { RunId = req.RunId, Command = req.Command, TaskId = req.TaskId, RunDir = req.RunDir, Life = life, Log = log, Ctx = ctx, MaxDrainSeconds = req.MaxDrainSeconds };

                log.LineWritten += (seq, line) => OnLine(rec, seq, line);
                life.Transitioned += t => OnTransition(rec, t);

                _recent.AddFirst(rec);
                while (_recent.Count > MaxRecent) { var last = _recent.Last.Value; _recent.RemoveLast(); try { last.Log.Dispose(); } catch { } }
                _active = rec;
            }

            Run.Current = rec.Ctx;
            try { rec.Commands = new CommandRecorder(rec); rec.Commands.Start(); } catch (Exception ex) { HostLog.Write("command recorder failed to start: " + ex.Message); }
            rec.Life.MarkInvoking();
            WriteRunJson(rec);
            HostLog.Write("run " + rec.RunId + " " + rec.Command + " dispatched");
            _app.Civil.RunCommand(executor, rec.Command, rec.Ctx,
                onStarted: () => { try { rec.Life.MarkRunning(); } catch (Exception ex) { HostLog.Write("MarkRunning: " + ex.Message); } },
                onReturned: err => { try { rec.Life.RunReturned(err); } catch (Exception ex) { HostLog.Write("RunReturned: " + ex.Message); } });
            return rec;
        }

        private void OnLine(RunRecord rec, long seq, string line)
        {
            lock (_sync)
            {
                rec.Lines.Add(line);
                if (rec.Lines.Count > RingSize) { rec.Lines.RemoveRange(0, rec.Lines.Count - RingSize); rec.FirstSeqInRing = seq - RingSize + 1; }
                Monitor.PulseAll(_sync);
            }
            if (line.IndexOf("\"src\":\"host\"", StringComparison.Ordinal) < 0) rec.Pinger?.NoteActivity();
            Raise(rec);
        }

        private void OnTransition(RunRecord rec, RunTransition t)
        {
            var st = _app.State;
            if (RunLifecycle.IsTerminalState(t.To))
            {
                st.ActiveRunId = null; st.ActiveRunState = null; st.ActiveRunSinceUtc = null;
                try { rec.DrainTimer?.Dispose(); } catch { }
                try { rec.Pinger?.Stop(); } catch { }
                try { rec.Commands?.Stop(); } catch { }
                if (ReferenceEquals(Run.Current, rec.Ctx)) Run.Current = null;
                HostLog.Write("run " + rec.RunId + " " + t.To.ToString().ToLowerInvariant() + " by " + t.By + (t.Inferred ? " (inferred)" : ""));
            }
            else
            {
                st.ActiveRunId = rec.RunId; st.ActiveRunState = t.To.ToString().ToLowerInvariant(); st.ActiveRunSinceUtc = t.AtUtc;
                if (t.To == RunState.Draining)
                {
                    rec.DrainingSinceUtc = t.AtUtc;
                    int maxMs = Math.Max(10, rec.MaxDrainSeconds) * 1000;
                    rec.DrainTimer = new Timer(_ => rec.Life.TryComplete(RunStatus.Failed, "drain-timeout", false, null, "no completion within " + rec.MaxDrainSeconds + " s"), null, maxMs, Timeout.Infinite);
                    try { rec.Pinger = new IdlePinger(_app, rec); rec.Pinger.Start(); } catch (Exception ex) { HostLog.Write("idle pinger failed to start: " + ex.Message); }
                }
            }
            WriteRunJson(rec);
            _app.Registry?.Write();
            lock (_sync) { Monitor.PulseAll(_sync); }
            Raise(rec);
        }

        private void WriteRunJson(RunRecord rec)
        {
            try
            {
                var extra = new Dictionary<string, object>
                {
                    ["taskId"] = rec.TaskId,
                    ["instance"] = new Dictionary<string, object> { ["pid"] = _app.State.Pid, ["port"] = _app.State.Port, ["version"] = _app.State.CivilVersion },
                    ["loadedVersion"] = _app.State.PayloadVersion,
                    ["idlePing"] = rec.Pinger?.Summary(),
                    ["commands"] = rec.Commands == null ? null : new Dictionary<string, object> { ["total"] = rec.Commands.Total, ["running"] = rec.Commands.Running, ["byName"] = rec.Commands.Summary() },
                };
                RunDocument.Write(Path.Combine(rec.RunDir, "run.json"), rec.Life, extra, rec.Log.Seq);
            }
            catch (Exception ex) { HostLog.Write("run.json write failed: " + ex.Message); }
        }

        private void Raise(RunRecord rec)
        {
            var h = Changed;
            if (h != null) { try { h(rec); } catch { } }
        }

        /// <summary>Block until the run is terminal or new lines arrived past sinceSeq, or timeout.</summary>
        public void Wait(RunRecord rec, int timeoutMs, long sinceSeq)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_sync)
            {
                while (!rec.Life.IsTerminal && rec.Log.Seq <= sinceSeq)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return;
                    Monitor.Wait(_sync, left);
                }
            }
        }

        public List<string> LinesSince(RunRecord rec, long sinceSeq, int max)
        {
            lock (_sync)
            {
                var res = new List<string>();
                int start = (int)Math.Max(0, sinceSeq + 1 - rec.FirstSeqInRing);
                for (int i = start; i < rec.Lines.Count && res.Count < max; i++) res.Add(rec.Lines[i]);
                return res;
            }
        }

        public bool Complete(RunRecord rec, string status, string by, string reason, bool inferred = false)
        {
            return rec.Life.TryComplete(status, by, inferred, null, reason);
        }
    }
}
