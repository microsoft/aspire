// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Agents.OpenCode;

/// <summary>
/// Interface for running OpenCode CLI commands.
/// </summary>
internal interface IOpenCodeCliRunner
{
    /// <summary>
    /// Gets the version of the OpenCode CLI if it is installed.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version of the OpenCode CLI, or null if it is not installed or an error occurred.</returns>
    Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken);
}
