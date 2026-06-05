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
    public static Hex1bWidget Build<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session,
        SessionTerminalRegistry terminals)
        where TParent : Hex1bWidget
    {
        // If a prior boot of the PTY task crashed (e.g. shell binary
        // missing), show the error here instead of returning an empty
        // TerminalWidget — the empty widget renders as a blank black
        // rectangle with no indication that anything went wrong, which is
        // indistinguishable from "shell still booting".
        if (session.TerminalCrashMessage is { Length: > 0 } crash)
        {
            return ctx.VStack(v =>
            [
                v.Text(""),
                v.Text("  [ Terminal failed to start ]"),
                v.Text(""),
                v.Text($"  {crash}"),
            ]);
        }

        var entry = GetOrCreateTerminal(session, terminals);
        // .Fill() forces the TerminalNode to take the full bounds of its
        // parent container (window body or tab content area). Without it the
        // widget defaults to its content size (the 80x24 we pass to
        // WithDimensions), leaving blank chrome around it and — critically —
        // not resizing the PTY when the window grows. TerminalNode.Arrange
        // resizes the underlying handle to match its layout bounds, so
        // .Fill() is both the visual and the PTY-size fix.
        var terminalWidget = new TerminalWidget(entry.Handle).Fill();

        // Stack a thin header row above the terminal so the user has a
        // visible reference for which workspace they're in and a one-click
        // way to open it in their editor. The header is rendered every
        // frame; it's just a couple of text + button nodes so the cost is
        // negligible compared to the PTY itself.
        return ctx.VStack(v =>
        [
            BuildHeader(v, session),
            terminalWidget,
        ]);
    }

    private static Hex1bWidget BuildHeader<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session)
        where TParent : Hex1bWidget
    {
        var workspaceRoot = session.Workspace?.Root;
        var label = $"  cwd: {TruncateWorkspaceLabel(workspaceRoot)}  ";

        return ctx.HStack(h =>
        [
            h.Text(label),
            // SplitButton primary = VS Code (the common case); secondary
            // dropdown contains VS Code Insiders. Both launch the editor
            // with the workspace root as the open folder so the user can
            // inspect logs, packages, and the dogfood.json manifest
            // without leaving the terminal.
            h.SplitButton()
                .PrimaryAction("Open in Code", _ => TryLaunchEditor("code", workspaceRoot))
                .SecondaryAction("Open in Code Insiders", _ => TryLaunchEditor("code-insiders", workspaceRoot)),
        ]);
    }

    /// <summary>
    /// Compresses the workspace path display down to the
    /// <c>aspire-dogfood-</c> prefix plus the last 8 characters of the
    /// random suffix CreateTempSubdirectory generates. Full paths on macOS
    /// look like
    /// <c>/var/folders/3c/abc1xyz4567/T/aspire-dogfood-X9aB1cD2eF3gHi/</c>
    /// — long enough to wrap the header on a narrow terminal. The unique
    /// suffix is the only part that disambiguates one session from another
    /// (the prefix is constant) so 8 chars is more than enough for the
    /// user to correlate against <c>dogfood.json</c>.
    /// </summary>
    private static string TruncateWorkspaceLabel(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return "(no workspace)";
        }
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Length <= 8)
        {
            return name;
        }
        // Show only the trailing 8 chars (the unique randomness) with a
        // leading ellipsis. e.g. "…X9aB1cD2".
        return "…" + name[^8..];
    }

    private static void TryLaunchEditor(string command, string? workspaceRoot)
    {
        // No workspace → nothing to open. Silently no-op rather than
        // launching the editor with no folder argument (which would open a
        // blank window).
        if (string.IsNullOrEmpty(workspaceRoot))
        {
            return;
        }
        try
        {
            // UseShellExecute=false lets Process.Start resolve `code` /
            // `code-insiders` from PATH on every platform. The CLI helper
            // (`code` on PATH) is the canonical install path Microsoft
            // documents (https://code.visualstudio.com/docs/setup/mac
            // → "Launching from the command line"). If the helper isn't
            // installed the Win32Exception is swallowed — there's no good
            // place to surface the failure from inside a render pass, and
            // the user can fall back to opening the path manually from
            // the terminal.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(workspaceRoot);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch
        {
            // Editor not installed / not on PATH — swallow. The TUI has no
            // toast surface to report this through and the user can open
            // the directory by other means.
        }
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
            .WithScrollback(5000)
            .WithDimensions(80, 24)
            .WithPtyProcess(opts =>
            {
                opts.FileName = shell.FileName;
                opts.Arguments ??= new List<string>();
                foreach (var a in shell.Arguments)
                {
                    opts.Arguments.Add(a);
                }
                // Drop the shell into the per-session workspace so anything
                // the user runs (`aspire new`, `dotnet new`, etc.) lands in
                // a clean directory alongside the build log, packages, and
                // dogfood.json manifest — keeping every artifact from one
                // dogfood run in one place that the user can hand to a bug
                // report. Falls back to the parent cwd when preparation
                // hasn't run yet (shouldn't happen — terminal only opens
                // after preparation succeeds — but defensive).
                opts.WorkingDirectory = session.Workspace?.Root;
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
        // the PTY rather than racing the boot. A crash in either task is
        // captured onto session.TerminalCrashMessage rather than silently
        // swallowed so the user can see *why* the shell never started
        // (e.g. an invalid DOGFOODER_SHELL path) instead of just an empty
        // black rectangle.
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await terminal.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path; nothing to do.
            }
            catch (Exception ex)
            {
                session.TerminalCrashMessage = $"Terminal crashed: {ex.GetType().Name}: {ex.Message}";
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
