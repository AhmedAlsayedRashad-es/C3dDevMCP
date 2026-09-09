using System.Collections.Generic;

namespace C3dMCP.Sdk
{
    /// <summary>The ambient current run, set by the host around each invocation. Use it from code
    /// that has no <see cref="IRunContext"/> in hand (services deep inside a payload). Null when no
    /// run is open; every facade call is then a no-op.</summary>
    public static class Run
    {
        private static readonly object Sync = new object();
        private static IRunContext _current;

        public static IRunContext Current
        {
            get { lock (Sync) { return _current; } }
            set { lock (Sync) { _current = value; } }
        }
    }

    /// <summary>Static facade over <see cref="Run.Current"/> so existing payload code that called
    /// <c>C3DPatch.Reporting.Feedback.*</c> migrates with a namespace swap.</summary>
    public static class Feedback
    {
        public static void Create(string objectType, string handle, string label = null,
            string container = null, IDictionary<string, object> data = null, string source = null)
            => Run.Current?.Feedback("create", objectType, handle, label, container, data);

        public static void Modify(string objectType, string handle, string label = null,
            string container = null, IDictionary<string, object> data = null, string source = null)
            => Run.Current?.Feedback("modify", objectType, handle, label, container, data);

        public static void Erase(string objectType, string handle, string label = null)
            => Run.Current?.Feedback("erase", objectType, handle, label);

        public static void Note(string label, IDictionary<string, object> data = null)
            => Run.Current?.Note(label, data);

        public static void Diagnostic(string name, bool? ok = null, IDictionary<string, object> data = null)
            => Run.Current?.Diagnostic(name, ok ?? false, data);

        public static void Log(string message, string level = "info")
            => Run.Current?.Log(message, level);

        /// <summary>The old Feedback.Result: ends the run with a status.</summary>
        public static void Result(string status, IDictionary<string, object> data = null)
            => Run.Current?.Complete(status, data);

        public static void Defer(int quietSeconds = 3) => Run.Current?.Defer(quietSeconds);
    }
}
