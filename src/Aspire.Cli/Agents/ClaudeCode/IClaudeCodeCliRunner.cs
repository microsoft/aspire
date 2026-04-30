// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Interface for running Claude Code CLI commands.
/// </summary>
internal interface IClaudeCodeCliRunner
{
    /// <summary>
    /// Gets the version of the Claude Code CLI if it is installed.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version of the Claude Code CLI, or null if it is not installed or an error occurred.</returns>
    Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken);
}
