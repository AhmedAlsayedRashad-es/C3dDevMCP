using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using C3dMCP.Engine;

namespace C3dMCP.Host.Palette
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RunItem : Bindable
    {
        public string RunId { get; set; }
        public string Command { get; set; }
        private string _state; public string State { get { return _state; } set { Set(ref _state, value); } }
        private string _by; public string By { get { return _by; } set { Set(ref _by, value); } }
        private string _when; public string When { get { return _when; } set { Set(ref _when, value); } }
        private string _duration; public string Duration { get { return _duration; } set { Set(ref _duration, value); } }
        public string ByLine => (By ?? "") + (When == null ? "" : " - " + When);
        public void Touch() { Raise(nameof(ByLine)); }
    }

    /// <summary>One rendered log line. These MUST be properties: WPF data binding ignores fields,
    /// so a field-backed LogItem renders 61 empty rows.</summary>
    public sealed class LogItem
    {
        public long Seq { get; set; }
        public string Time { get; set; }
        public string Src { get; set; }
        public string Text { get; set; }
        public bool FailedDiag { get; set; }
        public bool Late { get; set; }
    }

    public sealed class EscalationCard : Bindable
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Round1Reason { get; set; }
        public string Round1Checks { get; set; }
        public string Round2Reason { get; set; }
        public string Round2Checks { get; set; }
    }

    /// <summary>Everything the palette binds to. Refreshed from HostState and RunManager on the
    /// dispatcher thread; holds only values.</summary>
    public sealed class PaletteViewModel : Bindable
    {
        private readonly HostApp _app;
        public PaletteViewModel(HostApp app) { _app = app; }

        // Instance
        private string _civil; public string Civil { get { return _civil; } set { Set(ref _civil, value); } }
        public int Pid => _app.State.Pid;
        private string _port; public string Port { get { return _port; } set { Set(ref _port, value); } }
        private string _listener; public string Listener { get { return _listener; } set { Set(ref _listener, value); } }
        private bool _listening; public bool Listening { get { return _listening; } set { Set(ref _listening, value); } }
        private string _drawing; public string Drawing { get { return _drawing; } set { Set(ref _drawing, value); } }
        private bool _mainThreadOk = true; public bool MainThreadOk { get { return _mainThreadOk; } set { Set(ref _mainThreadOk, value); } }
        private string _copied; public string Copied { get { return _copied; } set { Set(ref _copied, value); } }

        // Payload
        private string _payload; public string Payload { get { return _payload; } set { Set(ref _payload, value); } }
        private string _payloadWhen; public string PayloadWhen { get { return _payloadWhen; } set { Set(ref _payloadWhen, value); } }
        private string _reloadError; public string ReloadError { get { return _reloadError; } set { Set(ref _reloadError, value); } }
        private bool _canReloadLast; public bool CanReloadLast { get { return _canReloadLast; } set { Set(ref _canReloadLast, value); } }

        // Runs
        public ObservableCollection<RunItem> Runs { get; } = new ObservableCollection<RunItem>();
        private RunItem _selected;
        public RunItem Selected { get { return _selected; } set { if (Set(ref _selected, value)) RebuildLog(); } }
        private string _runsCount; public string RunsCount { get { return _runsCount; } set { Set(ref _runsCount, value); } }

        // Log
        public ObservableCollection<LogItem> Log { get; } = new ObservableCollection<LogItem>();
        private string _logTitle; public string LogTitle { get { return _logTitle; } set { Set(ref _logTitle, value); } }
        private string _filter = ""; public string Filter { get { return _filter; } set { if (Set(ref _filter, value)) RebuildLog(); } }
        private bool _failedOnly; public bool FailedOnly { get { return _failedOnly; } set { if (Set(ref _failedOnly, value)) RebuildLog(); } }
        private bool _autoScroll = true; public bool AutoScroll { get { return _autoScroll; } set { Set(ref _autoScroll, value); } }
        public HashSet<string> SrcOff { get; } = new HashSet<string>();
        private string _counts; public string Counts { get { return _counts; } set { Set(ref _counts, value); } }

        // Actions
        private bool _canFinish; public bool CanFinish { get { return _canFinish; } set { Set(ref _canFinish, value); } }
        private bool _canReset; public bool CanReset { get { return _canReset; } set { Set(ref _canReset, value); } }
        private string _idleText; public string IdleText { get { return _idleText; } set { Set(ref _idleText, value); } }
        private int _idleStreak; public int IdleStreak { get { return _idleStreak; } set { Set(ref _idleStreak, value); } }
        private string _actionsTitle; public string ActionsTitle { get { return _actionsTitle; } set { Set(ref _actionsTitle, value); } }

        // Escalation
        private EscalationCard _escalation; public EscalationCard Escalation { get { return _escalation; } set { Set(ref _escalation, value); Raise(nameof(HasEscalation)); } }
        public bool HasEscalation => _escalation != null;

        // Footer
        private string _hostLog; public string HostLog { get { return _hostLog; } set { Set(ref _hostLog, value); } }
        public string DataFolder => C3dPaths.Root;
        private string _hostVersion; public string HostVersion { get { return _hostVersion; } set { Set(ref _hostVersion, value); } }

        private readonly Dictionary<string, long> _lastSeq = new Dictionary<string, long>();

        /// <summary>Pull everything from the host. Must run on the dispatcher.</summary>
        public void Refresh()
        {
            var st = _app.State;
            Civil = "Civil 3D " + st.CivilVersion;
            Port = st.Port > 0 ? st.Port.ToString() : "-";
            Listening = st.ListenerState == "listening";
            Listener = (st.ListenerState ?? "").ToUpperInvariant();
            Drawing = string.IsNullOrEmpty(st.Drawing) ? "(no drawing)" : System.IO.Path.GetFileName(st.Drawing);
            Payload = st.PayloadName == null ? "none loaded" : st.PayloadVersion;
            PayloadWhen = st.PayloadLoadedUtc.HasValue ? "Loaded today at " + st.PayloadLoadedUtc.Value.ToLocalTime().ToString("HH:mm:ss.fff") : "";
            ReloadError = st.LastReloadError == null ? "Last reload error - none" : "Last reload error - " + st.LastReloadError;
            CanReloadLast = st.LastReloadDll != null && (_app.Runs == null || !_app.Runs.HasNonTerminalRun);
            HostVersion = "host " + st.HostVersion;
            HostLog = string.Join(Environment.NewLine, Host.HostLog.Tail(3));

            var recent = _app.Runs?.Recent() ?? new List<RunManager.RunRecord>();
            var ids = new HashSet<string>(recent.Select(r => r.RunId));
            for (int i = Runs.Count - 1; i >= 0; i--) if (!ids.Contains(Runs[i].RunId)) Runs.RemoveAt(i);
            int idx = 0;
            foreach (var rec in recent)
            {
                var item = Runs.FirstOrDefault(x => x.RunId == rec.RunId);
                if (item == null) { item = new RunItem { RunId = rec.RunId, Command = rec.Command }; Runs.Insert(idx, item); }
                else if (Runs.IndexOf(item) != idx) { Runs.Remove(item); Runs.Insert(idx, item); }
                idx++;
                var l = rec.Life;
                item.State = l.State.ToString().ToLowerInvariant();
                item.By = l.By ?? (l.State == RunState.Draining ? "draining" : "");
                item.When = l.CreatedUtc.ToLocalTime().ToString("HH:mm:ss");
                var end = l.EndedUtc ?? DateTime.UtcNow;
                item.Duration = (end - l.CreatedUtc).ToString(@"mm\:ss\.fff");
                item.Touch();
            }
            RunsCount = recent.Count + " RECENT";
            if (Selected == null || !ids.Contains(Selected.RunId)) Selected = Runs.FirstOrDefault();
            else RebuildLog(incremental: true);

            var active = _app.Runs?.Active;
            bool draining = active != null && active.Life.State == RunState.Draining;
            CanFinish = draining;
            CanReset = active == null || active.Life.IsTerminal;
            ActionsTitle = active == null ? "" : "RUN " + active.RunId;
            if (draining && active.Pinger != null)
            {
                var j = active.Pinger.Judge;
                IdleStreak = Math.Min(3, j.Streak);
                IdleText = "idle " + Math.Min(3, j.Streak) + "/" + j.RequiredStreak + ", last " + Math.Round(j.LastMs) + " ms" + (j.Probes == 0 ? " (waiting)" : "");
            }
            else { IdleStreak = 0; IdleText = draining ? "idle 0/3, waiting for quiet period" : "no run draining"; }
        }

        private void RebuildLog(bool incremental = false)
        {
            var rec = Selected == null ? null : _app.Runs?.Find(Selected.RunId);
            if (rec == null) { Log.Clear(); LogTitle = "LIVE LOG"; Counts = ""; return; }
            long have; _lastSeq.TryGetValue(rec.RunId, out have);
            if (!incremental || have > rec.Log.Seq) { Log.Clear(); have = 0; }
            var lines = _app.Runs.LinesSince(rec, have, 100000);
            foreach (var line in lines)
            {
                var item = Parse(line);
                if (item == null) continue;
                have = Math.Max(have, item.Seq);
                if (Show(item)) Log.Add(item);
            }
            _lastSeq[rec.RunId] = have;
            LogTitle = "RUN " + rec.RunId + " - " + rec.Log.Seq + " ENTRIES";
            Counts = Log.Count + " shown";
        }

        public void RefilterLog() { _lastSeq.Remove(Selected?.RunId ?? ""); RebuildLog(); }

        private bool Show(LogItem it)
        {
            if (SrcOff.Contains(it.Src)) return false;
            if (FailedOnly && !it.FailedDiag) return false;
            if (!string.IsNullOrEmpty(Filter) && it.Text.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static LogItem Parse(string line)
        {
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var r = doc.RootElement;
                    var it = new LogItem { Seq = r.GetProperty("seq").GetInt64(), Src = r.GetProperty("src").GetString() };
                    DateTime t; if (r.TryGetProperty("t", out var tp) && DateTime.TryParse(tp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) it.Time = t.ToLocalTime().ToString("HH:mm:ss.fff");
                    it.Late = r.TryGetProperty("late", out var lp) && lp.ValueKind == JsonValueKind.True;
                    string data = r.TryGetProperty("data", out var dp) && dp.ValueKind != JsonValueKind.Null ? " data=" + dp.GetRawText() : "";
                    switch (it.Src)
                    {
                        case "log": it.Text = Str(r, "msg"); break;
                        case "note": it.Text = Str(r, "msg") + data; break;
                        case "diag":
                            bool ok = r.TryGetProperty("ok", out var okp) && okp.ValueKind == JsonValueKind.True;
                            it.FailedDiag = !ok;
                            it.Text = "ok=" + (ok ? "true" : "false") + " name=" + Str(r, "name") + data; break;
                        case "feedback": it.Text = Str(r, "op") + " " + Str(r, "type") + " " + Str(r, "handle") + (Str(r, "label") != "" ? " \"" + Str(r, "label") + "\"" : "") + data; break;
                        case "host":
                            var evt = Str(r, "evt");
                            if (evt == "state") it.Text = "state " + Str(r, "state").ToLowerInvariant();
                            else if (evt == "idle-ping") it.Text = "idle-ping " + Str(r, "ms") + " ms " + Str(r, "verdict") + " streak=" + Str(r, "streak");
                            else if (evt == "complete") it.Text = "complete " + Str(r, "status") + " by " + Str(r, "by") + (Str(r, "inferred") == "true" ? " (inferred)" : "") + (Str(r, "reason") != "" ? " - " + Str(r, "reason") : "");
                            else it.Text = evt + " " + line;
                            break;
                        default: it.Text = line; break;
                    }
                    if (it.Late) it.Text = "[late] " + it.Text;
                    return it;
                }
            }
            catch { return null; }
        }

        private static string Str(JsonElement r, string name)
        {
            JsonElement p;
            if (!r.TryGetProperty(name, out p) || p.ValueKind == JsonValueKind.Null) return "";
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }
    }
}
