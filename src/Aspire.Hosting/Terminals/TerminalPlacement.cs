// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Identifies where a terminal is displayed in the dashboard.
/// </summary>
/// <remarks>
/// Placement is a property of the view, not of the workload: terminals with different
/// <see cref="TerminalOwner"/> values can share a placement, and a terminal can in principle move between
/// placements without its workload being affected.
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public enum TerminalPlacement
{
    /// <summary>
    /// The terminal is a tab in the dashboard's terminal dock, and is listed by the terminal watch stream.
    /// </summary>
    Dock,

    /// <summary>
    /// The terminal belongs to an <see cref="InputType.Terminal"/> interaction input and is displayed inside
    /// that interaction's dialog. These are addressed directly by the dialog and are deliberately excluded
    /// from the dock's tab list.
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
    /// Terminals driven purely through the automation members of <see cref="IAspireTerminal"/> never need a
    /// viewer. Giving that case its own value keeps it out of the dock's tab list without having to pretend it
    /// belongs to a dialog or a resource.
    /// </remarks>
    None
}
