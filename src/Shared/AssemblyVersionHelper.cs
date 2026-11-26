// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Aspire.Shared;

/// <summary>
/// Helpers for reading assembly and runtime version information.
/// </summary>
internal static class AssemblyVersionHelper
{
    private static readonly Lazy<string?> s_runtimeVersion = new(GetRuntimeVersion);

    internal static string? RuntimeVersion => s_runtimeVersion.Value;

    /// <summary>
    /// Gets the informational version (e.g. "8.0.0-preview.2.23619.3+commit") from an assembly.
    /// </summary>
    internal static string GetInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }

    /// <summary>
    /// Gets the file version (build ID) from an assembly.
    /// </summary>
    internal static string GetFileVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? string.Empty;
    }

    /// <summary>
    /// Gets a user-friendly display version from an assembly, stripping the commit hash suffix.
    /// Falls back to file version, then assembly version.
    /// </summary>
    internal static string? GetDisplayVersion(Assembly assembly)
    {
        // The package version is stamped into the assembly's AssemblyInformationalVersionAttribute at build time, followed by a '+' and
        // the commit hash, e.g.:
        // [assembly: AssemblyInformationalVersion("8.0.0-preview.2.23604.7+e7762a46d31842884a0bc72c92e07ba700c99bf5")]

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is not null)
        {
            var plusIndex = version.IndexOf('+');

            if (plusIndex > 0)
            {
                return version[..plusIndex];
            }

            return version;
        }

        // Fallback to the file version, which is based on the CI build number, and then fallback to the assembly version, which is
        // product stable version, e.g. 8.0.0.0
        version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetCustomAttribute<AssemblyVersionAttribute>()?.Version;

        return version;
    }

    private static string? GetRuntimeVersion()
    {
        var description = RuntimeInformation.FrameworkDescription;

        // The version is the last token, e.g. ".NET 8.0.3" or ".NET Core 3.1.32".
        var lastSpace = description.LastIndexOf(' ');
        return lastSpace >= 0 && lastSpace < description.Length - 1 ? description[(lastSpace + 1)..] : null;
    }
}
