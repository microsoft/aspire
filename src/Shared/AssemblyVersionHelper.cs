// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Shared;

/// <summary>
/// Helpers for reading version information from assemblies.
/// </summary>
internal static class AssemblyVersionHelper
{
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
}
