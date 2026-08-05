// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace Aspire.Cli;

/// <summary>
/// Abstracts environment queries so both <see cref="CliExecutionContext"/> and
/// <see cref="Acquisition.IdentityResolver"/> can read environment variables
/// and detect the host OS without a circular dependency between them.
/// </summary>
public interface IEnvironment
{
    /// <summary>
    /// Gets the value of an environment variable.
    /// </summary>
    /// <param name="variable">The environment variable name.</param>
    /// <returns>The value, or <see langword="null"/> if not set.</returns>
    string? GetEnvironmentVariable(string variable);

    /// <summary>
    /// Gets all environment variables.
    /// </summary>
    IEnumerable<(string Name, string? Value)> GetEnvironmentVariables();

    /// <summary>
    /// Gets a value indicating whether the current OS is Windows.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    bool IsWindows();

    /// <summary>
    /// Gets a value indicating whether the current OS is Linux.
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    bool IsLinux();

    /// <summary>
    /// Gets a value indicating whether the current OS is macOS.
    /// </summary>
    [SupportedOSPlatformGuard("macos")]
    bool IsMacOS();
}
