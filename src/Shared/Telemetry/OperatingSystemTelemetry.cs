// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Shared.Telemetry;

internal static class OperatingSystemTelemetry
{
    /// <summary>
    /// Gets the human-readable operating system name for the <c>os.name</c> semantic convention.
    /// </summary>
    public static string GetOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        return RuntimeInformation.OSDescription;
    }

    /// <summary>
    /// Gets the OpenTelemetry semantic convention value for the <c>os.type</c> attribute.
    /// </summary>
    public static string GetOsType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "darwin";
        }

        return "unknown";
    }

    public static string GetOsVersion() => Environment.OSVersion.Version.ToString();
}
