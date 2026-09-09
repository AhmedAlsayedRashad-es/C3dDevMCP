using System;
using System.Collections.Generic;
using C3dMCP.Sdk;

namespace C3dMCP.Engine
{
    public enum RunState { Created, Invoking, Running, Draining, Completed, Failed, Abandoned }

    /// <summary>One transition, as raised to listeners and written to the run log.</summary>
    public sealed class RunTransition
    {
        public RunState From;
        public RunState To;
        public string Status;    // set on terminal transitions
        public string By;        // payload | host | idle-ping | button | unlock | shutdown | drain-timeout
        public bool Inferred;
        public string Reason;
        public DateTime AtUtc;
    }

    /// <summary>The run state machine. Created → Invoking → Running → (Completed | Failed | Draining);
    /// Draining → (Completed | Failed | Abandoned). There is no timeout state: a timeout is a
    /// caller's wait ending, never a run state. Terminal is sticky: the first completion wins and
    /// later attempts return false (the caller logs them as late). Thread-safe.</summary>
    public sealed class RunLifecycle
    {
        private readonly object _sync = new object();

        public RunLifecycle(string runId, string command, Func<DateTime> clock = null)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("runId is required", nameof(runId));
            RunId = runId;
            Command = command ?? string.Empty;
            _clock = clock ?? (() => DateTime.UtcNow);
            CreatedUtc = _clock();
        }

        private readonly Func<DateTime> _clock;

        public string RunId { get; }
        public string Command { get; }
        public RunState State { get; private set; } = RunState.Created;
        public string Status { get; private set; }
        public string By { get; private set; }
        public bool Inferred { get; private set; }
        public string Reason { get; private set; }
        public bool Deferred { get; private set; }
        public int QuietSeconds { get; private set; } = 3;
        public DateTime CreatedUtc { get; }
        public DateTime? StartedUtc { get; private set; }
        public DateTime? RunReturnedUtc { get; private set; }
        public DateTime? EndedUtc { get; private set; }
        public string Error { get; private set; }
        public IDictionary<string, object> Data { get; private set; }

        public event Action<RunTransition> Transitioned;

        public bool IsTerminal
        {
            get { lock (_sync) { return IsTerminalState(State); } }
        }

        public static bool IsTerminalState(RunState s) =>
            s == RunState.Completed || s == RunState.Failed || s == RunState.Abandoned;

        public void MarkInvoking()
        {
            Move(RunState.Created, RunState.Invoking, null, "host", false, null);
        }

        public void MarkRunning()
        {
            lock (_sync) { StartedUtc = _clock(); }
            Move(RunState.Invoking, RunState.Running, null, "host", false, null);
        }

        /// <summary>Payload declares an async tail. Legal only while Running; ignored otherwise.</summary>
        public bool Defer(int quietSeconds)
        {
            lock (_sync)
            {
                if (State != RunState.Running) return false;
                Deferred = true;
                QuietSeconds = Math.Max(0, quietSeconds);
                return true;
            }
        }

        /// <summary>Called by the host when Run() returned (or threw). Decides Completed, Failed or
        /// Draining. If the payload already completed inside Run(), this is a no-op.</summary>
        public void RunReturned(Exception error)
        {
            RunTransition t = null;
            lock (_sync)
            {
                RunReturnedUtc = _clock();
                if (IsTerminalState(State)) return;
                if (State != RunState.Running) return;
                if (error != null)
                {
                    Error = error.GetType().Name + ": " + error.Message;
                    t = Terminal(RunStatus.Failed, "host", false, "Run() threw", null);
                }
                else if (Deferred)
                {
                    t = new RunTransition { From = State, To = RunState.Draining, By = "host", AtUtc = _clock() };
                    State = RunState.Draining;
                }
                else
                {
                    t = Terminal(RunStatus.Completed, "host", false, "Run() returned", null);
                }
            }
            Raise(t);
        }

        /// <summary>Attempt a terminal transition. Returns false when the run is already terminal
        /// (a late completion) or has not started.</summary>
        public bool TryComplete(string status, string by, bool inferred = false, IDictionary<string, object> data = null, string reason = null)
        {
            RunTransition t;
            lock (_sync)
            {
                if (IsTerminalState(State)) return false;
                if (State == RunState.Created || State == RunState.Invoking) return false;
                t = Terminal(string.IsNullOrWhiteSpace(status) ? RunStatus.Completed : status, by, inferred, reason, data);
            }
            Raise(t);
            return true;
        }

        private RunTransition Terminal(string status, string by, bool inferred, string reason, IDictionary<string, object> data)
        {
            var to = StateFor(status);
            var t = new RunTransition { From = State, To = to, Status = status, By = by, Inferred = inferred, Reason = reason, AtUtc = _clock() };
            State = to;
            Status = status;
            By = by;
            Inferred = inferred;
            Reason = reason;
            Data = data == null ? null : new Dictionary<string, object>(data);
            EndedUtc = t.AtUtc;
            return t;
        }

        public static RunState StateFor(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case RunStatus.Failed: return RunState.Failed;
                case RunStatus.Abandoned: return RunState.Abandoned;
                default: return RunState.Completed;   // "completed" and any custom word
            }
        }

        private void Move(RunState from, RunState to, string status, string by, bool inferred, string reason)
        {
            RunTransition t;
            lock (_sync)
            {
                if (State != from)
                    throw new InvalidOperationException($"Run {RunId}: cannot move {State} -> {to} (expected {from}).");
                t = new RunTransition { From = from, To = to, Status = status, By = by, Inferred = inferred, Reason = reason, AtUtc = _clock() };
                State = to;
            }
            Raise(t);
        }

        private void Raise(RunTransition t)
        {
            if (t == null) return;
            var h = Transitioned;
            if (h == null) return;
            try { h(t); } catch { /* listeners never break the machine */ }
        }
    }
}
