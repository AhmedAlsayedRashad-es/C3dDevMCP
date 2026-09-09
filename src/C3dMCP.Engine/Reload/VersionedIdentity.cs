using C3dMCP.Sdk;
using System;
using System.IO;

namespace C3dMCP.Engine.Reload
{
    /// <summary>The versioned-assembly-identity convention: each payload build stamps its
    /// AssemblyName as <c>{AddinName}_{version}</c> (e.g. <c>MyAddin_r5</c>) while keeping the file
    /// named <c>{AddinName}.dll</c>, so many builds of "the same" payload coexist in one
    /// non-unloadable process without an identity collision. This confirms a loaded payload's
    /// stamped identity belongs to the expected add-in and extracts its version suffix. Pure logic —
    /// the live host reads the identity via <c>AssemblyName.GetAssemblyName(dllPath).Name</c> and
    /// passes it here. Unit-tested.</summary>
    public static class VersionedIdentity
    {
        /// <summary>Validates that <paramref name="actualIdentity"/> is a versioned build of
        /// <paramref name="addinName"/> (the <c>{AddinName}_...</c> convention) and returns the
        /// version suffix (e.g. <c>r5</c>). Throws <see cref="InvalidDataException"/> on a blank
        /// identity or one that doesn't belong to the add-in; throws <see cref="ArgumentException"/>
        /// on a blank add-in name.</summary>
        public static string ValidateAndExtractVersion(string actualIdentity, string addinName)
        {
            if (string.IsNullOrWhiteSpace(addinName))
                throw new ArgumentException("Add-in name is required.", nameof(addinName));
            if (string.IsNullOrWhiteSpace(actualIdentity))
                throw new InvalidDataException("The payload assembly has no identity name.");

            var prefix = addinName + "_";
            if (!actualIdentity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || actualIdentity.Length == prefix.Length)
            {
                throw new InvalidDataException(
                    "Payload identity '" + actualIdentity + "' is not a versioned build of add-in '"
                    + addinName + "' (expected '" + prefix + "<version>').");
            }

            return actualIdentity.Substring(prefix.Length);
        }
    }
}
