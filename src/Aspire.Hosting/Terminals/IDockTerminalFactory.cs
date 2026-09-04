// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

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
    /// Describes a new dock terminal.
    /// </summary>
    /// <param name="title">A caller-supplied title, or <see langword="null"/> to use the factory's default.</param>
    /// <param name="ordinal">A 1-based counter of dock terminals created so far, for default titles.</param>
    DockTerminalDefinition Create(string? title, int ordinal);
}

/// <summary>
/// A dock terminal's title and configured workload.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="TerminalLaunchOptions"/>. That type is public and describes a workload as a
/// <see cref="TerminalCommand"/> — a child process — precisely so Hex1b stays out of Aspire's public API.
/// The dock's built-in terminal is an in-process Hex1b app rather than a process, so it needs the builder
/// directly, and that has to stay on an internal path.
/// </remarks>
internal sealed record DockTerminalDefinition(string Title, Hex1bTerminalBuilder Builder);
