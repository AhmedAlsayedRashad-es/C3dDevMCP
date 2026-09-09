using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using C3dMCP.Engine;

namespace C3dMCP.Host
{
    /// <summary>Writes instances\&lt;pid&gt;.json so the MCP server can find live instances without a
    /// broadcast. Rewritten on every transition and every 30 s; deleted at Terminate. The server
    /// checks pid + process start ticks before trusting a file.</summary>
    public sealed class InstanceRegistry : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly HostState _state;
        private readonly string _path;
        private System.Threading.Timer _timer;

        public InstanceRegistry(HostState state)
        {
            _state = state;
            _path = C3dPaths.InstanceFile(state.Pid);
        }

        public string Path => _path;

        public void Start()
        {
            Write();
            _timer = new System.Threading.Timer(_ => Write(), null, 30000, 30000);
        }

        public sealed class Record
        {
            public int Pid { get; set; }
            public long ProcessStartTicks { get; set; }
            public int Port { get; set; }
            public string Token { get; set; }
            public string Version { get; set; }
            public string Exe { get; set; }
            public string HostVersion { get; set; }
            public string Listener { get; set; }
            public DateTime Started { get; set; }
            public DateTime Updated { get; set; }
            public string Drawing { get; set; }
            public PayloadRecord Payload { get; set; }
            public ActiveRunRecord ActiveRun { get; set; }
        }
        public sealed class PayloadRecord { public string Name { get; set; } public string LoadedVersion { get; set; } public DateTime? LoadedAt { get; set; } }
        public sealed class ActiveRunRecord { public string RunId { get; set; } public string State { get; set; } public DateTime? Since { get; set; } }

        public Record Snapshot() => new Record
        {
            Pid = _state.Pid,
            ProcessStartTicks = _state.ProcessStartTicks,
            Port = _state.Port,
            Token = _state.Token,
            Version = _state.CivilVersion,
            Exe = _state.Exe,
            HostVersion = _state.HostVersion,
            Listener = _state.ListenerState,
            Started = _state.StartedUtc,
            Updated = DateTime.UtcNow,
            Drawing = _state.Drawing,
            Payload = _state.PayloadName == null ? null : new PayloadRecord { Name = _state.PayloadName, LoadedVersion = _state.PayloadVersion, LoadedAt = _state.PayloadLoadedUtc },
            ActiveRun = _state.ActiveRunId == null ? null : new ActiveRunRecord { RunId = _state.ActiveRunId, State = _state.ActiveRunState, Since = _state.ActiveRunSinceUtc },
        };

        public void Write()
        {
            try { AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(Snapshot(), Json)); }
            catch (Exception ex) { HostLog.Write("instance file write failed: " + ex.Message); }
        }

        public void Dispose()
        {
            try { _timer?.Dispose(); } catch { }
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
