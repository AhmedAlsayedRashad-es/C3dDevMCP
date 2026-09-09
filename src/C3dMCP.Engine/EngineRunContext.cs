using System;
using System.Collections.Generic;
using C3dMCP.Sdk;

namespace C3dMCP.Engine
{
    /// <summary>The host's IRunContext: binds a <see cref="RunLifecycle"/> to a <see cref="RunLog"/>.
    /// Every report becomes one JSONL line; lines after the terminal state are written with
    /// late:true and never change the outcome. Never throws to the payload.</summary>
    public sealed class EngineRunContext : IRunContext
    {
        private readonly RunLifecycle _life;
        private readonly RunLog _log;

        public EngineRunContext(RunLifecycle life, RunLog log, IReadOnlyDictionary<string, string> args = null)
        {
            _life = life ?? throw new ArgumentNullException(nameof(life));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            Args = args ?? new Dictionary<string, string>();
            _life.Transitioned += OnTransition;
        }

        public RunLifecycle Lifecycle => _life;
        public RunLog Log => _log;

        public string RunId => _life.RunId;
        public string Command => _life.Command;
        public IReadOnlyDictionary<string, string> Args { get; }
        public bool IsOpen => !_life.IsTerminal;

        private long _activity;
        /// <summary>Count of payload reports (Log/Note/Diagnostic/Feedback), excluding host lines,
        /// for the idle-ping activity snapshot.</summary>
        public long ActivitySeq => System.Threading.Interlocked.Read(ref _activity);
        private void Bump() => System.Threading.Interlocked.Increment(ref _activity);

        void IRunContext.Log(string message, string level) => Safe(() =>
            { Bump(); _log.Append("log", Fields("lvl", level ?? "info", "msg", message ?? string.Empty), late: _life.IsTerminal); });

        public void Diagnostic(string name, bool ok, IDictionary<string, object> data = null) => Safe(() =>
            { Bump(); _log.Append("diag", Fields("name", name, "ok", ok, "data", data), late: _life.IsTerminal); });

        public void Note(string label, IDictionary<string, object> data = null) => Safe(() =>
            { Bump(); _log.Append("note", Fields("msg", label, "data", data), late: _life.IsTerminal); });

        public void Feedback(string op, string objectType, string handle, string label = null,
            string container = null, IDictionary<string, object> data = null) => Safe(() =>
            { Bump(); _log.Append("feedback", Fields("op", op, "type", objectType, "handle", handle, "label", label, "container", container, "data", data),
                late: _life.IsTerminal); });

        public void Defer(int quietSeconds = 3) => Safe(() =>
        {
            bool ok = _life.Defer(quietSeconds);
            _log.Append("host", Fields("evt", "defer", "quietSeconds", quietSeconds, "accepted", ok));
        });

        public void Complete(string status, IDictionary<string, object> data = null) => Safe(() =>
        {
            if (!_life.TryComplete(status, "payload", false, data))
                _log.Append("host", Fields("evt", "complete-late", "status", status, "data", data), late: true);
        });

        private void OnTransition(RunTransition t)
        {
            if (RunLifecycle.IsTerminalState(t.To))
                _log.Append("host", Fields("evt", "complete", "state", t.To, "status", t.Status, "by", t.By,
                    "inferred", t.Inferred ? (object)true : null, "reason", t.Reason, "data", _life.Data, "error", _life.Error));
            else if (t.To == RunState.Draining)
                _log.Append("host", Fields("evt", "state", "state", t.To, "quietSeconds", _life.QuietSeconds));
            else
                _log.Append("host", Fields("evt", "state", "state", t.To));
        }

        private static List<KeyValuePair<string, object>> Fields(params object[] kv)
        {
            var list = new List<KeyValuePair<string, object>>(kv.Length / 2);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                list.Add(new KeyValuePair<string, object>((string)kv[i], kv[i + 1]));
            return list;
        }

        private static void Safe(Action a) { try { a(); } catch { } }
    }
}
