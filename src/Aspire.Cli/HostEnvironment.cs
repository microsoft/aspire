// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace Aspire.Cli;

/// <summary>
/// Default <see cref="IEnvironment"/> that delegates to the process
/// environment and <see cref="OperatingSystem"/> runtime checks.
/// </summary>
public sealed class HostEnvironment : IEnvironment
{
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);

    public IEnumerable<(string Name, string? Value)> GetEnvironmentVariables()
    {
        return Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Name: e.Key?.ToString() ?? string.Empty, Value: e.Value?.ToString()));
    }

    [SupportedOSPlatformGuard("windows")]
    public bool IsWindows() => OperatingSystem.IsWindows();

    [SupportedOSPlatformGuard("linux")]
    public bool IsLinux() => OperatingSystem.IsLinux();

    [SupportedOSPlatformGuard("macos")]
    public bool IsMacOS() => OperatingSystem.IsMacOS();
}
