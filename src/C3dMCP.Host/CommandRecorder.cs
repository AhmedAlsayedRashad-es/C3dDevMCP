using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace C3dMCP.Host
{
    /// <summary>Records every AutoCAD command that executes while a run is open, so the log answers
    /// "which commands ran, and which did not". A payload drives native work through
    /// SendStringToExecute (_AECCADDFITTING, _AECCADDAPPURTENANCE, ...); the host cannot see the
    /// send, but it sees the execution: CommandWillStart / CommandEnded / CommandCancelled /
    /// CommandFailed. A command that starts and never ends is still running; no starts at all means
    /// the queue is not draining.
    ///
    /// Lines are written as src="cmd": {"evt":"end","name":"AECCADDFITTING","ms":412,"n":37}.
    /// The idle-ping sentinel is excluded — it is not the payload's work and would flood the log.</summary>
    public sealed class CommandRecorder
    {
        private readonly RunManager.RunRecord _rec;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
        private Document _doc;
        private bool _hooked;
        private string _open;              // the command currently executing, if any
        private Stopwatch _clock;
        private long _total;

        public CommandRecorder(RunManager.RunRecord rec) { _rec = rec; }

        public sealed class Stat
        {
            public int Ran, Cancelled, Failed;
            public double TotalMs, MaxMs;
        }

        public long Total { get { lock (_sync) { return _total; } } }
        public string Running { get { lock (_sync) { return _open; } } }

        public Dictionary<string, Stat> Snapshot()
        {
            lock (_sync) { return new Dictionary<string, Stat>(_stats, StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>Compact per-command tally for run.json and the palette.</summary>
        public List<Dictionary<string, object>> Summary(int max = 12)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var kv in Snapshot())
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = kv.Key, ["ran"] = kv.Value.Ran,
                    ["cancelled"] = kv.Value.Cancelled == 0 ? null : (object)kv.Value.Cancelled,
                    ["failed"] = kv.Value.Failed == 0 ? null : (object)kv.Value.Failed,
                    ["totalMs"] = Math.Round(kv.Value.TotalMs), ["maxMs"] = Math.Round(kv.Value.MaxMs),
                });
            list.Sort((a, b) => Convert.ToDouble(b["totalMs"]).CompareTo(Convert.ToDouble(a["totalMs"])));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        public void Start() { RealCivil3DHost.PostToMainThread(Hook); }
        public void Stop()
        {
            RealCivil3DHost.PostToMainThread(Unhook);
            var summary = Summary();
            if (summary.Count > 0)
                _rec.Log.Append("cmd", "evt", "summary", "commands", Total, "byName", summary);
        }

        private void Hook()
        {
            try
            {
                _doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (_doc == null) return;
                _doc.CommandWillStart += OnStart;
                _doc.CommandEnded += OnEnded;
                _doc.CommandCancelled += OnCancelled;
                _doc.CommandFailed += OnFailed;
                _hooked = true;
            }
            catch (Exception ex) { HostLog.Write("command recorder hook failed: " + ex.Message); }
        }

        private void Unhook()
        {
            if (!_hooked || _doc == null) return;
            try
            {
                _doc.CommandWillStart -= OnStart;
                _doc.CommandEnded -= OnEnded;
                _doc.CommandCancelled -= OnCancelled;
                _doc.CommandFailed -= OnFailed;
            }
            catch { }
            _hooked = false;
        }

        private static bool Skip(string name) =>
            string.IsNullOrEmpty(name) || string.Equals(name, IdlePinger.Sentinel, StringComparison.OrdinalIgnoreCase);

        private void OnStart(object s, CommandEventArgs e)
        {
            if (Skip(e.GlobalCommandName)) return;
            lock (_sync) { _open = e.GlobalCommandName; _clock = Stopwatch.StartNew(); _total++; }
            _rec.Log.Append("cmd", "evt", "start", "name", e.GlobalCommandName, "n", Total);
        }

        private void OnEnded(object s, CommandEventArgs e) => Close(e, "end");
        private void OnCancelled(object s, CommandEventArgs e) => Close(e, "cancel");
        private void OnFailed(object s, CommandEventArgs e) => Close(e, "fail");

        private void Close(CommandEventArgs e, string how)
        {
            if (Skip(e.GlobalCommandName)) return;
            double ms;
            lock (_sync)
            {
                ms = _clock?.Elapsed.TotalMilliseconds ?? 0;
                _open = null;
                if (!_stats.TryGetValue(e.GlobalCommandName, out var st)) _stats[e.GlobalCommandName] = st = new Stat();
                if (how == "end") st.Ran++; else if (how == "cancel") st.Cancelled++; else st.Failed++;
                st.TotalMs += ms;
                if (ms > st.MaxMs) st.MaxMs = ms;
            }
            _rec.Log.Append("cmd", "evt", how, "name", e.GlobalCommandName, "ms", Math.Round(ms, 1), "n", Total);
        }
    }
}
