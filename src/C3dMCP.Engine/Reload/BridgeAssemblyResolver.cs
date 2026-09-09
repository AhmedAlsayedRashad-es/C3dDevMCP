using System;
using System.Collections.Generic;
using System.Reflection;

namespace C3dMCP.Engine.Reload
{
    /// <summary>The bridge libraries (C3dMCP.Sdk, C3dMCP.Engine) must be exactly ONE copy per
    /// process, the host's, so a reloaded payload's IRunContext / IC3dPatchService types stay
    /// type-identical to the host's. Decides whether a requested assembly is handed the host's
    /// loaded copy or resolved from the payload folder. Pure logic; unit-tested.</summary>
    public static class BridgeAssemblyResolver
    {
        private static readonly HashSet<string> Bridge = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "C3dMCP.Sdk",
            "C3dMCP.Engine",
        };

        public static bool IsBridgeAssembly(string simpleName) => simpleName != null && Bridge.Contains(simpleName);

        public static Assembly Resolve(AssemblyName requested, IDictionary<string, Assembly> hostBridgeAssemblies)
        {
            if (requested == null || hostBridgeAssemblies == null) return null;
            if (!IsBridgeAssembly(requested.Name)) return null;
            return hostBridgeAssemblies.TryGetValue(requested.Name, out var host) ? host : null;
        }
    }
}
