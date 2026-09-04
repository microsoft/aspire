// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Produces the terminal that opens when the dashboard's terminal dock creates a new tab.
/// </summary>
/// <remarks>
/// Indirected through an interface so the dock's default experience can change (today a built-in TUI,
/// later a real Aspire REPL) without <see cref="TerminalService"/> knowing anything about it.
/// </remarks>
internal interface IDockTerminalFactory
{
    /// <summary>
    /// Creates the launch options for a new dock terminal.
    /// </summary>
    /// <param name="title">A caller-supplied title, or <see langword="null"/> to use the factory's default.</param>
    /// <param name="ordinal">A 1-based counter of dock terminals created so far, for default titles.</param>
    TerminalLaunchOptions Create(string? title, int ordinal);
}
