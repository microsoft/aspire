// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Describes a terminal to be created by <see cref="TerminalService"/>.
/// </summary>
/// <remarks>
/// <see cref="Builder"/> takes a Hex1b type directly. That is a deliberate spike shortcut: it keeps the
/// workload description expressive without designing an Aspire-shaped equivalent up front. It is also the
/// last remaining Hex1b leak on this path — <see cref="IAspireTerminal"/> already hides Hex1b from
/// everything downstream, so closing this one is what would make <see cref="TerminalService"/> publishable.
/// </remarks>
internal sealed class TerminalLaunchOptions
{
    /// <summary>
    /// Gets or sets the title shown on the terminal's dock tab.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets the configured workload. Aspire attaches the transport itself, so callers must not
    /// call <c>WithHmp1Server</c> or <c>Build</c> on the builder.
    /// </summary>
    public required Hex1bTerminalBuilder Builder { get; set; }

    /// <summary>
    /// Gets or sets the surface the terminal is displayed on. Defaults to <see cref="TerminalSurface.Dock"/>.
    /// </summary>
    public TerminalSurface Surface { get; set; } = TerminalSurface.Dock;
}
