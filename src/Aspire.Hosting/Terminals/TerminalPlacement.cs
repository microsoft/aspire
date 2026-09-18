// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Identifies where a terminal is displayed in the dashboard.
/// </summary>
/// <remarks>
/// Placement is fixed when a terminal is created. It describes where the terminal is displayed,
/// while <see cref="TerminalOwner"/> identifies which component owns its workload.
/// </remarks>
[Experimental(TerminalDiagnostics.DiagnosticId, UrlFormat = TerminalDiagnostics.UrlFormat)]
public enum TerminalPlacement
{
    /// <summary>
    /// The terminal is a tab in the dashboard's terminal dock, and is listed by the terminal watch stream.
    /// </summary>
    Dock,

    /// <summary>
    /// The terminal can be displayed by <see cref="IInteractionService.PromptTerminalAsync"/>.
    /// It remains caller-owned and is addressed directly by the dialog, not listed in the terminal dock.
    /// </summary>
    Dialog,

    /// <summary>
    /// The terminal is displayed on the terminal view of the resource it belongs to.
    /// </summary>
    ResourceView,

    /// <summary>
    /// The terminal is not displayed anywhere.
    /// </summary>
    /// <remarks>
    /// Terminals driven purely through the automation members of <see cref="AspireTerminal"/> never need a
    /// viewer. Giving that case its own value keeps it out of the dock's tab list without having to pretend it
    /// belongs to a dialog or a resource.
    /// </remarks>
    None
}
