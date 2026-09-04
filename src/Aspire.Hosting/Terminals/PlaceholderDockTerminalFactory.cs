// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The default dock terminal: a small in-process TUI that stands in for the built-in Aspire REPL.
/// </summary>
/// <remarks>
/// <para>
/// This is a placeholder. It exists to prove the dock end-to-end — that the AppHost can create a terminal,
/// that the dashboard discovers it over the watch stream, that the HMP1 tunnel renders it in xterm.js, and
/// that keystrokes travel back — without also having to design what an Aspire REPL should actually do.
/// </para>
/// <para>
/// Because it runs as a Hex1b app rather than a PTY process, there is no child process to manage and it
/// behaves identically on every platform.
/// </para>
/// </remarks>
internal sealed class PlaceholderDockTerminalFactory : IDockTerminalFactory
{
    public TerminalLaunchOptions Create(string? title, int ordinal)
    {
        var resolvedTitle = title ?? (ordinal == 1 ? "Aspire" : $"Aspire {ordinal}");

        return new TerminalLaunchOptions
        {
            Title = resolvedTitle,
            Surface = TerminalSurface.Dock,
            Builder = Hex1bTerminal.CreateBuilder()
                .WithHex1bApp(ctx => BuildPlaceholderApp(ctx, resolvedTitle))
        };
    }

    private static Hex1bWidget BuildPlaceholderApp(RootContext ctx, string title)
    {
        var body = ctx.Center(
            ctx.Border(b =>
            [
                b.VStack(v =>
                [
                    v.Text(""),
                    v.Text("  The built-in Aspire REPL lives here.  "),
                    v.Text(""),
                    v.Text("  This placeholder proves the dock, the  "),
                    v.Text("  watch stream, and the HMP1 tunnel.     "),
                    v.Text(""),
                ])
            ]).Title($" {title} "));

        var info = ctx.InfoBar(s =>
        [
            s.Section(title),
            s.Spacer(),
            s.Section("placeholder"),
        ]).Divider(" ");

        // Bind a key so the terminal visibly accepts focus and input even though the placeholder has
        // nothing to do with it. Without a binding the app never requests a redraw, which makes a working
        // tunnel look indistinguishable from a dead one.
        return ctx.VStack(v => [body.Fill(), info]).InputBindings(bindings =>
        {
            bindings.Key(Hex1bKey.Enter).Action(_ => { }, "Refresh");
        });
    }
}
