using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using C3dMCP.Engine;
using C3dMCP.Sdk;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace C3dMCP.Host
{
    /// <summary>While a run drains, sends the C3DMCP_IDLEPING sentinel through the command queue
    /// and measures CommandEnded. Activity counters (run log seq, non-sentinel commands, database
    /// changes) feed IdlePingJudge; three quiet probes over 10 s infer completion.</summary>
    public sealed class IdlePinger
    {
        public const string Sentinel = "C3DMCP_IDLEPING";
        private readonly HostApp _app;
        private readonly RunManager.RunRecord _rec;
        private readonly IdlePingJudge _judge = new IdlePingJudge();
        private Timer _timer;
        private long _commands, _dbChanges;
        private int _probeOutstanding;
        private Stopwatch _probeClock;
        private Timer _probeBudget;
        private Document _doc;
        private bool _hooked;
        private int _periodMs = 5000;

        public IdlePinger(HostApp app, RunManager.RunRecord rec) { _app = app; _rec = rec; }

        public IdlePingJudge Judge => _judge;

        public void Start()
        {
            int quiet = Math.Max(0, _rec.Life.QuietSeconds) * 1000;
            _timer = new Timer(_ => Tick(), null, Math.Max(1000, quiet), _periodMs);
            RealCivil3DHost.PostToMainThread(Hook);
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            try { _probeBudget?.Dispose(); } catch { }
            RealCivil3DHost.PostToMainThread(Unhook);
        }

        /// <summary>Any run-log line resets the streak eagerly (the judge also sees it via LogSeq).</summary>
        public void NoteActivity() { _judge.Reset(); }

        public Dictionary<string, object> Summary() => new Dictionary<string, object>
        {
            ["probes"] = _judge.Probes,
            ["streak"] = _judge.Streak,
            ["required"] = _judge.RequiredStreak,
            ["lastMs"] = Math.Round(_judge.LastMs, 1),
            ["complete"] = _judge.IsComplete,
        };

        private void Hook()
        {
            try
            {
                _doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (_doc == null) return;
                _doc.CommandWillStart += OnCommandWillStart;
                _doc.CommandEnded += OnCommandDone;
                _doc.CommandCancelled += OnCommandDone;
                _doc.CommandFailed += OnCommandDone;
                _doc.Database.ObjectAppended += OnDb;
                _doc.Database.ObjectModified += OnDb;
                _doc.Database.ObjectErased += OnDbErased;
                _hooked = true;
            }
            catch (Exception ex) { HostLog.Write("idle pinger hook failed: " + ex.Message); }
        }

        private void Unhook()
        {
            if (!_hooked || _doc == null) return;
            try
            {
                _doc.CommandWillStart -= OnCommandWillStart;
                _doc.CommandEnded -= OnCommandDone;
                _doc.CommandCancelled -= OnCommandDone;
                _doc.CommandFailed -= OnCommandDone;
                _doc.Database.ObjectAppended -= OnDb;
                _doc.Database.ObjectModified -= OnDb;
                _doc.Database.ObjectErased -= OnDbErased;
            }
            catch { }
            _hooked = false;
        }

        private void OnDb(object s, ObjectEventArgs e) { Interlocked.Increment(ref _dbChanges); }
        private void OnDbErased(object s, ObjectErasedEventArgs e) { Interlocked.Increment(ref _dbChanges); }

        private void OnCommandWillStart(object s, CommandEventArgs e)
        {
            if (!string.Equals(e.GlobalCommandName, Sentinel, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _commands);
        }

        private void OnCommandDone(object s, CommandEventArgs e)
        {
            if (!string.Equals(e.GlobalCommandName, Sentinel, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _probeOutstanding, 0) == 0) return;
            try { _probeBudget?.Dispose(); } catch { }
            double ms = _probeClock?.Elapsed.TotalMilliseconds ?? 0;
            Observe(new IdleProbe { AtUtc = DateTime.UtcNow, ElapsedMs = ms, TimedOut = false, LogSeq = _rec.Ctx.ActivitySeq, CommandCount = Interlocked.Read(ref _commands), DbChangeCount = Interlocked.Read(ref _dbChanges) });
        }

        private void Tick()
        {
            if (_rec.Life.IsTerminal) { Stop(); return; }
            if (_rec.Life.State != RunState.Draining) return;
            if (!_hooked || _doc == null) return;
            if (Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) != 0) return;
            _probeClock = Stopwatch.StartNew();
            _probeBudget = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _probeOutstanding, 0) == 0) return;
                Observe(new IdleProbe { AtUtc = DateTime.UtcNow, ElapsedMs = 60000, TimedOut = true, LogSeq = _rec.Ctx.ActivitySeq, CommandCount = Interlocked.Read(ref _commands), DbChangeCount = Interlocked.Read(ref _dbChanges) });
            }, null, 60000, Timeout.Infinite);
            try { _doc.SendStringToExecute(Sentinel + "\n", true, false, false); }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _probeOutstanding, 0);
                HostLog.Write("idle ping send failed: " + ex.Message);
            }
        }

        /// <summary>One probe on demand (the palette's "Idle ping now" and /v1/idle-ping). Returns
        /// null when a probe is already outstanding.</summary>
        public void ProbeNow() { Tick(); }

        private void Observe(IdleProbe p)
        {
            var verdict = _judge.Observe(p);
            _rec.Log.Append("host", "evt", "idle-ping", "ms", Math.Round(p.ElapsedMs, 1), "idle", verdict == IdleVerdict.Idle || verdict == IdleVerdict.Complete,
                "verdict", verdict.ToString().ToLowerInvariant(), "streak", _judge.Streak, "cmds", p.CommandCount, "dbChanges", p.DbChangeCount);
            if (_judge.Probes >= 10 && _periodMs != 15000) { _periodMs = 15000; try { _timer?.Change(_periodMs, _periodMs); } catch { } }
            if (verdict == IdleVerdict.Complete)
                _rec.Life.TryComplete(RunStatus.Completed, "idle-ping", true, null, "3 quiet probes over 10 s with no activity");
            _app.PaletteHost?.Refresh();
        }
    }
}
