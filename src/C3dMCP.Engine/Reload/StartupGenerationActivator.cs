using C3dMCP.Sdk;
using System;

namespace C3dMCP.Engine.Reload
{
    public static class StartupGenerationActivator
    {
        public static RefreshLifecycle Activate(
            ExecutorGeneration candidate,
            Func<object, bool> start)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            if (start == null)
                throw new ArgumentNullException(nameof(start));

            return start(candidate.Application)
                ? new RefreshLifecycle(candidate)
                : null;
        }
    }
}
