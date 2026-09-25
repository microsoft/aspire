// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Maui;

/// <summary>
/// Well-known MSBuild property names used when launching MAUI platform resources.
/// </summary>
internal static class KnownMauiMSBuildProperties
{
    /// <summary>
    /// MSBuild property that selects which Android device or emulator <c>dotnet run</c> targets.
    /// Passed via the adb-style switches it forwards (e.g. <c>-d</c>, <c>-e</c>, <c>-s &lt;serial&gt;</c>).
    /// See: https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-10#dotnet-run-support.
    /// </summary>
    public const string AdbTarget = "AdbTarget";

    /// <summary>
    /// MSBuild property that selects which iOS device or simulator <c>dotnet run</c> targets.
    /// Devices use the raw UDID; simulators use the <c>:v2:udid=&lt;UDID&gt;</c> form.
    /// See: https://learn.microsoft.com/dotnet/maui/ios/cli#launch-the-app-on-a-device.
    /// </summary>
    public const string DeviceName = "_DeviceName";

    /// <summary>
    /// MSBuild property that specifies the runtime identifier (e.g. <c>ios-arm64</c> for physical iOS devices).
    /// </summary>
    public const string RuntimeIdentifier = "RuntimeIdentifier";

    /// <summary>
    /// MSBuild property holding the single target framework of a project.
    /// </summary>
    public const string TargetFramework = "TargetFramework";

    /// <summary>
    /// MSBuild property holding the semicolon-separated list of target frameworks of a multi-targeted project.
    /// </summary>
    public const string TargetFrameworks = "TargetFrameworks";
}
