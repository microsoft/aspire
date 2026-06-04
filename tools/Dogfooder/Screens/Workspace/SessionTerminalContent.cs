// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Builds the window-body widget for a session in its <em>terminal</em>
/// state: the embedded PTY plus a <see cref="Hex1bTerminalAutomator"/> that
/// types the identity-override commands into the spawned shell on first
/// launch. Subsequent renders of the same window reuse the cached terminal.
/// </summary>
internal static class SessionTerminalContent
{
    public static Hex1bWidget Build(
        WindowContentContext<Hex1bWidget> ctx,
        DogfoodSession session,
        SessionTerminalRegistry terminals)
    {
        _ = ctx; // window context not currently needed for the terminal body.

        var entry = GetOrCreateTerminal(session, terminals);
        return new TerminalWidget(entry.Handle);
    }

    private static SessionTerminalRegistry.Entry GetOrCreateTerminal(
        DogfoodSession session,
        SessionTerminalRegistry terminals)
    {
        if (terminals.TryGet(session.Id, out var existing))
        {
            return existing;
        }

        var shell = ShellCommandFormatter.ResolveDefault();

        var builder = Hex1bTerminal.CreateBuilder()
            .WithTerminalWidget(out var handle)
            .WithMouse()
            .WithDimensions(80, 24)
            .WithPtyProcess(opts =>
            {
                opts.FileName = shell.FileName;
                opts.Arguments ??= new List<string>();
                foreach (var a in shell.Arguments)
                {
                    opts.Arguments.Add(a);
                }
                // Intentionally do NOT set opts.Environment here. The
                // dogfooding overrides are applied by typing shell commands
                // after launch so they are visible in the terminal scrollback.
                // The child inherits the parent's environment verbatim.
            });

        var terminal = builder.Build();
        session.Status = SessionStatus.Running;

        // Drive the embedded terminal lifecycle on a background task; the
        // render path must remain synchronous. The automator runs as a
        // sibling task once the terminal is alive so its typing shows up in
        // the PTY rather than racing the boot.
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await terminal.RunAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                session.Status = SessionStatus.Exited;
            }
        }, cts.Token);

        if (session.Plan is { } plan)
        {
            _ = Task.Run(() => ApplyOverridesAsync(terminal, shell.Kind, plan, cts.Token), cts.Token);
        }

        var entry = new SessionTerminalRegistry.Entry(terminal, handle, cts);
        terminals.Register(session.Id, entry);
        return entry;
    }

    private static async Task ApplyOverridesAsync(
        Hex1bTerminal terminal,
        ShellCommandFormatter.ShellKind shellKind,
        SessionEnvironmentPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            // Wait for the shell to reach a usable state before typing. We
            // can't rely on a single prompt regex across bash/zsh/pwsh/cmd
            // (PS1 customisations defeat anything specific), so we use a
            // short fixed delay. The user sees the commands appear once the
            // prompt is rendered; if the shell is still booting, the input
            // is queued by the PTY and runs at the first prompt anyway.
            await automator.WaitAsync(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);

            foreach (var line in ShellCommandFormatter.Format(shellKind, plan))
            {
                // SlowTypeAsync makes the typing visible as keystrokes
                // landing one-at-a-time, which is the whole point of doing
                // this on the foreground rather than via opts.Environment.
                await automator.SlowTypeAsync(line, TimeSpan.FromMilliseconds(8), cancellationToken).ConfigureAwait(false);
                await automator.EnterAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path; nothing to do.
        }
        catch
        {
            // Don't crash the host on automator failure — the user can still
            // type the commands manually if something goes wrong.
        }
    }
}
