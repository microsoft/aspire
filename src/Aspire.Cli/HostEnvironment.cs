// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

/// <summary>
/// Default <see cref="IEnvironment"/> that delegates to the process
/// environment and <see cref="OperatingSystem"/> runtime checks.
/// </summary>
internal sealed class HostEnvironment : IEnvironment
{
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool IsMacOS => OperatingSystem.IsMacOS();
}
