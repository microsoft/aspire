// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The dashboard-visible description of a terminal.
/// </summary>
internal sealed record TerminalDescriptor(string Id, string Title);

/// <summary>
/// The kind of change that occurred to the set of dock terminals.
/// </summary>
internal enum TerminalChangeType
{
    /// <summary>A terminal was created.</summary>
    Added,

    /// <summary>A terminal was disposed and should be removed from the dock.</summary>
    Removed,

    /// <summary>An existing terminal's title changed.</summary>
    Retitled,

    /// <summary>
    /// <see cref="IAspireTerminal.Show"/> was called. Dashboards should reveal the dock and switch to
    /// this terminal's tab.
    /// </summary>
    Activated
}

/// <summary>
/// A change to the set of dock terminals, broadcast to every connected dashboard.
/// </summary>
internal sealed record TerminalChange(TerminalChangeType ChangeType, TerminalDescriptor Terminal);
