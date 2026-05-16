// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Shared;

namespace Aspire.Dashboard.Extensions;

internal static class AssemblyExtensions
{
    public static string? GetDisplayVersion(this Assembly assembly)
    {
        // The package version is stamped into the assembly's AssemblyInformationalVersionAttribute at build time, followed by a '+' and
        // the commit hash, e.g.:
        // [assembly: AssemblyInformationalVersion("8.0.0-preview.2.23604.7+e7762a46d31842884a0bc72c92e07ba700c99bf5")]

        var version = AssemblyVersionHelper.GetInformationalVersion(assembly);

        if (version.Length > 0)
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
        version = AssemblyVersionHelper.GetFileVersion(assembly);
        if (version.Length > 0)
        {
            return version;
        }

        return assembly.GetCustomAttribute<AssemblyVersionAttribute>()?.Version;
    }
}
