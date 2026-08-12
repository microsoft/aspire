// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents the annotation for the JavaScript package manager's install command.
/// </summary>
/// <param name="args">
/// The command line arguments for the JavaScript package manager's install command.
/// This includes the command itself (i.e. "install").
/// </param>
public sealed class JavaScriptInstallCommandAnnotation(string[] args) : IResourceAnnotation
{
    /// <summary>
    /// Gets the command-line arguments supplied to the JavaScript package manager.
    /// </summary>
    public string[] Args { get; } = args;

    /// <summary>
    /// Gets or sets the additional arguments for installing production-only dependencies (excluding devDependencies).
    /// This flag is appended to the base install command (from <see cref="Args"/>) when generating the
    /// production dependencies stage in the Dockerfile. Each package manager sets its own flag
    /// (e.g. npm uses <c>--omit=dev</c>, yarn uses <c>--production</c>, pnpm uses <c>--prod</c>).
    /// </summary>
    public string? ProductionInstallArgs { get; init; }
}
