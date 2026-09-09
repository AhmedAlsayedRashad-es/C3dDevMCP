using C3dMCP.Sdk;
using System;

namespace C3dMCP.Engine.Reload
{
    public sealed class ExecutorGeneration
    {
        public ExecutorGeneration(object application, object command, IC3dPatchService patchService,
            string addinName, string loadedVersion)
        {
            Application = application ?? throw new ArgumentNullException(nameof(application));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            PatchService = patchService ?? throw new ArgumentNullException(nameof(patchService));
            AddinName = RequireValue(addinName, nameof(addinName));
            LoadedVersion = RequireValue(loadedVersion, nameof(loadedVersion));
        }

        public object Application { get; }
        public object Command { get; }
        public IC3dPatchService PatchService { get; }
        public string AddinName { get; }
        public string LoadedVersion { get; }

        private static string RequireValue(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
            return value;
        }
    }
}
