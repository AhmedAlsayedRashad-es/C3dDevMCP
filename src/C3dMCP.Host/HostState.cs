using System;
using System.Diagnostics;

namespace C3dMCP.Host
{
    /// <summary>What this Civil3D instance knows about itself. Written to the instance file and
    /// answered by /v1/status. Single instance per process; fields are set from the main thread or
    /// the listener thread and read anywhere, so keep them simple values.</summary>
    public sealed class HostState
    {
        public HostState()
        {
            var p = Process.GetCurrentProcess();
            Pid = p.Id;
            try { ProcessStartTicks = p.StartTime.ToUniversalTime().Ticks; } catch { ProcessStartTicks = 0; }
            try { Exe = p.MainModule?.FileName; } catch { Exe = null; }
            StartedUtc = DateTime.UtcNow;
            Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public int Pid { get; }
        public long ProcessStartTicks { get; }
        public string Exe { get; }
        public DateTime StartedUtc { get; }
        public string Token { get; }
        public string CivilVersion { get; set; }
        public string HostVersion { get; set; }

        public int Port { get; set; }
        public string ListenerState { get; set; } = "starting";   // listening | acl-denied | failed
        public string ListenerError { get; set; }

        public string Drawing { get; set; }

        public string PayloadName { get; set; }
        public string PayloadVersion { get; set; }
        public DateTime? PayloadLoadedUtc { get; set; }
        public string LastReloadError { get; set; }
        public string LastReloadDll { get; set; }

        public string ActiveRunId { get; set; }
        public string ActiveRunState { get; set; }
        public DateTime? ActiveRunSinceUtc { get; set; }

        public bool BaselineValid { get; set; }
        public DateTime? BaselineMarkedUtc { get; set; }

        public bool SentinelVerified { get; set; }
        public double SentinelRoundTripMs { get; set; }
    }
}
