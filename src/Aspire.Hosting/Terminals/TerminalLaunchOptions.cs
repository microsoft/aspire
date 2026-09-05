// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Describes a terminal to be created by <see cref="TerminalService"/>.
/// </summary>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class TerminalLaunchOptions
{
    /// <summary>
    /// Gets or sets the title shown on the terminal's dock tab, and in the title bar when the terminal is
    /// detached into its own window.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets the process the terminal runs.
    /// </summary>
    public required TerminalCommand Command { get; set; }

    /// <summary>
    /// Gets or sets where the terminal is displayed. Defaults to <see cref="TerminalPlacement.Dock"/>.
    /// </summary>
    public TerminalPlacement Placement { get; set; } = TerminalPlacement.Dock;
}
