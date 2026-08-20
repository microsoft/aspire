// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aspire.Shared;

namespace Aspire.Dashboard.Utils;

public static class VersionHelpers
{
    private static readonly Lazy<string?> s_cachedRuntimeVersion = new Lazy<string?>(GetRuntimeVersion);

    public static string? DashboardDisplayVersion { get; } = AssemblyVersionHelper.GetDisplayVersion(typeof(VersionHelpers).Assembly);

    public static string? RuntimeVersion => s_cachedRuntimeVersion.Value;

    internal static string RuntimeFrameworkDescription { get; } = RuntimeInformation.FrameworkDescription;

    internal static bool IsNativeAot { get; } = !RuntimeFeature.IsDynamicCodeSupported;

    internal static string RuntimeDisplayName { get; } = GetRuntimeDisplayName(RuntimeFrameworkDescription, IsNativeAot);

    internal static string RuntimeMode { get; } = GetRuntimeMode(IsNativeAot);

    internal static string GetRuntimeDisplayName(string frameworkDescription, bool isNativeAot)
        => isNativeAot ? $"{frameworkDescription} (Native AOT)" : frameworkDescription;

    internal static string GetRuntimeMode(bool isNativeAot)
        => isNativeAot ? "Native AOT" : "JIT";

    private static string? GetRuntimeVersion()
    {
        var description = RuntimeInformation.FrameworkDescription;

        // Example inputs:
        // ".NET 8.0.3"
        // ".NET Core 3.1.32"
        // ".NET Framework 4.8.9032.0"

        int lastSpace = description.LastIndexOf(' ');
        if (lastSpace >= 0 && lastSpace < description.Length - 1)
        {
            return description.Substring(lastSpace + 1);
        }

        return null;
    }
}
