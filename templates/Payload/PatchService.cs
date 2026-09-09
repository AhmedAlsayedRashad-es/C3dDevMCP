using System;
using System.Collections.Generic;
using C3dMCP.Sdk;

namespace C3dPayload
{
    /// <summary>The payload's reload hook: Capture on the outgoing generation, SafeEnd to stop it,
    /// Refresh to activate the incoming one. This template keeps no cross-reload state.</summary>
    public sealed class PatchService : IC3dPatchService
    {
        public ReloadState Capture(object extensionApp) => new ReloadState { CapturedAt = DateTime.UtcNow, Data = new Dictionary<string, object>() };
        public void SafeEnd(object extensionApp, object appContext, ReloadState state) { }
        public void Refresh(object extensionApp, object appContext, ReloadState state) { }
    }
}
