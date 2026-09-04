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
    Interaction
}
