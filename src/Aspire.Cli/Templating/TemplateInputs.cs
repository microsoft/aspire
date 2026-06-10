// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Values passed to templates from commands.
/// </summary>
internal sealed class TemplateInputs
{
    /// <summary>
    /// Gets the project name (from --name option).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the output path (from --output option).
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Gets the NuGet source (from --source option).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the version (from --version option).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the channel name (from --channel option).
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>
    /// Gets the selected AppHost language identifier.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is an in-place initialization (e.g., aspire init)
    /// where the template should use the current working directory without prompting for name/output.
    /// </summary>
    public bool UseWorkingDirectory { get; init; }
}
