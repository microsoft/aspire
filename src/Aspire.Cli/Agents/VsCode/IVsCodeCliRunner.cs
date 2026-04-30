// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Options for running VS Code CLI commands.
/// </summary>
internal sealed class VsCodeRunOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to use VS Code Insiders instead of stable.
    /// </summary>
    public bool UseInsiders { get; set; }
}

/// <summary>
/// Interface for running VS Code CLI commands.
/// </summary>
internal interface IVsCodeCliRunner
{
    /// <summary>
    /// Gets the version of the VS Code CLI if it is installed.
    /// </summary>
    /// <param name="options">Options specifying which VS Code variant to check.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version of the VS Code CLI, or null if it is not installed or an error occurred.</returns>
    Task<SemVersion?> GetVersionAsync(VsCodeRunOptions options, CancellationToken cancellationToken);
}
