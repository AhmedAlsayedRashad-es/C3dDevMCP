using System.Collections.Generic;

namespace C3dMCP.Sdk
{
    /// <summary>The one channel a run reports through. Every call appends one line to the run's
    /// own <c>run.jsonl</c> (never shared across runs), streams it to the palette, and makes it
    /// queryable by the MCP. Safe from any thread; never throws.</summary>
    public interface IRunContext
    {
        string RunId { get; }
        string Command { get; }
        IReadOnlyDictionary<string, string> Args { get; }

        /// <summary>True until the run reached a terminal state.</summary>
        bool IsOpen { get; }

        /// <summary>Free log line. <paramref name="level"/>: info | warn | error | debug.</summary>
        void Log(string message, string level = "info");

        /// <summary>A named check with a verdict the analyzer reads by value.</summary>
        void Diagnostic(string name, bool ok, IDictionary<string, object> data = null);

        /// <summary>A labelled note with optional data (the old Feedback.Note).</summary>
        void Note(string label, IDictionary<string, object> data = null);

        /// <summary>An object mutation (the old Feedback.Create/Modify/Erase). <paramref name="op"/>:
        /// create | modify | erase.</summary>
        void Feedback(string op, string objectType, string handle, string label = null,
            string container = null, IDictionary<string, object> data = null);

        /// <summary>Declare an async tail: the run must stay open after Run returns. The host then
        /// waits for <see cref="Complete"/>, or infers completion by idle ping after
        /// <paramref name="quietSeconds"/> of no activity, or the user marks it finished.</summary>
        void Defer(int quietSeconds = 3);

        /// <summary>End the run. Idempotent: the first terminal call wins. <paramref name="status"/>
        /// is <see cref="RunStatus.Completed"/>, <see cref="RunStatus.Failed"/>, or a custom word.</summary>
        void Complete(string status, IDictionary<string, object> data = null);
    }

    public static class RunStatus
    {
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Abandoned = "abandoned";
    }
}
