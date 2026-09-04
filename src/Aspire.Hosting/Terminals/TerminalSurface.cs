// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Identifies where a terminal is displayed in the dashboard.
/// </summary>
internal enum TerminalSurface
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
    Interaction,

    /// <summary>
    /// The terminal is attached to a resource in the application model and is displayed on that resource's
    /// own terminal view rather than in the dock.
    /// </summary>
    /// <remarks>
    /// Nothing produces this value yet. It exists so that resource terminals — which today are owned by the
    /// DCP terminal host rather than by <see cref="TerminalService"/> — can be adopted into the same registry
    /// and exposed through <see cref="IAspireTerminal"/> for automation. Every surface check in
    /// <see cref="TerminalService"/> is written as an explicit test for <see cref="Dock"/>, so a resource
    /// terminal already behaves correctly by default: it stays out of the dock's tab list and
    /// <see cref="IAspireTerminal.Show"/> is a no-op for it.
    /// </remarks>
    Resource
}
