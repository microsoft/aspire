// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aspire.Shared;

namespace Aspire.Dashboard.Utils;

public static class VersionHelpers
{
    public static string? DashboardDisplayVersion { get; } = AssemblyVersionHelper.GetDisplayVersion(typeof(VersionHelpers).Assembly);

    public static string? RuntimeVersion => AssemblyVersionHelper.RuntimeVersion;

    internal static string RuntimeFrameworkDescription { get; } = RuntimeInformation.FrameworkDescription;

    internal static bool IsNativeAot { get; } = !RuntimeFeature.IsDynamicCodeSupported;

    internal static string RuntimeDisplayName { get; } = GetRuntimeDisplayName(RuntimeFrameworkDescription, IsNativeAot);

    internal static string RuntimeMode { get; } = GetRuntimeMode(IsNativeAot);

    internal static string GetRuntimeDisplayName(string frameworkDescription, bool isNativeAot)
        => isNativeAot ? $"{frameworkDescription} (AOT)" : frameworkDescription;

    internal static string GetRuntimeMode(bool isNativeAot)
        => isNativeAot ? "AOT" : "JIT";
}
