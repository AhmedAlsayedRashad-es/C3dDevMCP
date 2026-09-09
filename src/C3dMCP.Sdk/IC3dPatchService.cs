using System;
using System.Collections.Generic;

namespace C3dMCP.Sdk
{
    /// <summary>Implemented by the payload's <c>{AddinName}.PatchService</c>: how it captures its
    /// state, stops the old generation, and re-initializes the new one across a hot reload.</summary>
    public interface IC3dPatchService
    {
        ReloadState Capture(object extensionApp);
        void SafeEnd(object extensionApp, object appContext, ReloadState state);
        void Refresh(object extensionApp, object appContext, ReloadState state);
    }

    /// <summary>State the payload chooses to carry across a reload. Only <see cref="CapturedAt"/>
    /// is validated by the host.</summary>
    public sealed class ReloadState
    {
        public DateTime CapturedAt { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }
}
