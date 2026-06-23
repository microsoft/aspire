// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

/// <summary>
/// Abstracts environment queries so both <see cref="CliExecutionContext"/> and
/// <see cref="Acquisition.IdentityResolver"/> can read environment variables
/// and detect the host OS without a circular dependency between them.
/// </summary>
internal interface IEnvironment
{
    /// <summary>
    /// Gets the value of an environment variable.
    /// </summary>
    /// <param name="variable">The environment variable name.</param>
    /// <returns>The value, or <see langword="null"/> if not set.</returns>
    string? GetEnvironmentVariable(string variable);

    /// <summary>
    /// Gets a value indicating whether the current OS is Windows.
    /// </summary>
    bool IsWindows { get; }

    /// <summary>
    /// Gets a value indicating whether the current OS is Linux.
    /// </summary>
    bool IsLinux { get; }

    /// <summary>
    /// Gets a value indicating whether the current OS is macOS.
    /// </summary>
    bool IsMacOS { get; }
}
