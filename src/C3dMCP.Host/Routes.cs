using System;
using System.Collections.Generic;

namespace C3dMCP.Host
{
    /// <summary>The HTTP routes. Phase 1a: status and the host log. Later phases add reload, runs,
    /// wait, complete, idle-ping, baseline, reset.</summary>
    internal static class Routes
    {
        internal static void Register(HostApp app)
        {
            var api = app.Api;
            var st = app.State;

            api.Map("GET", "/v1/status", _ =>
            {
                double probeMs;
                bool responsive = RealCivil3DHost.ProbeMainThread(1500, out probeMs);
                return new
                {
                    pid = st.Pid,
                    version = st.CivilVersion,
                    hostVersion = st.HostVersion,
                    port = st.Port,
                    listener = st.ListenerState,
                    drawing = st.Drawing,
                    started = st.StartedUtc,
                    payload = st.PayloadName == null ? null : new { name = st.PayloadName, loadedVersion = st.PayloadVersion, loadedAt = st.PayloadLoadedUtc },
                    activeRun = st.ActiveRunId == null ? null : new { runId = st.ActiveRunId, state = st.ActiveRunState, since = st.ActiveRunSinceUtc },
                    baseline = new { valid = st.BaselineValid, markedAt = st.BaselineMarkedUtc },
                    mainThreadResponsive = responsive,
                    mainThreadProbeMs = Math.Round(probeMs, 1),
                    sentinel = new { verified = st.SentinelVerified, roundTripMs = st.SentinelRoundTripMs },
                };
            });

            api.Map("GET", "/v1/log", r =>
            {
                int tail = 50;
                int.TryParse(r.Query("tail", "50"), out tail);
                return new { lines = HostLog.Tail(Math.Max(1, Math.Min(200, tail))) };
            });
        }
    }
}
