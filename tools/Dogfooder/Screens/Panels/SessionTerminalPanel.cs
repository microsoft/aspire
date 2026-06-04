// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Panels;

/// <summary>
/// Embeds a Hex1b <see cref="Hex1bTerminal"/> in the right pane. After the
/// child shell starts, a <see cref="Hex1bTerminalAutomator"/> types the
/// identity-override commands (<c>export</c>/<c>$env:</c>/<c>set</c>) so the
/// dogfooding setup is visible in the scrollback rather than applied
/// silently via the PTY's initial environment.
/// </summary>
internal static class SessionTerminalPanel
{
    // Cache of session-id → embedded terminal. Module-scope because Build is
    // called every render and we must not spawn a fresh PTY per frame.
    // Disposal is best-effort on Dogfooder shutdown — the OS reaps children.
    private static readonly Dictionary<string, EmbeddedTerminal> s_terminals = new();

    public static Hex1bWidget Build(WidgetContext<VStackWidget> ctx, AppState state)
    {
        var session = state.ActiveSession;
        if (session is null)
        {
            return ctx.Text("(no active session)");
        }

        var embedded = GetOrCreateTerminal(session);

        return ctx.Border(b =>
        [
            b.Text($"Terminal — {session.Name}  [{session.Status}]"),
            b.Separator(),
            b.Terminal(embedded.Handle),
        ]);
    }

    private static EmbeddedTerminal GetOrCreateTerminal(DogfoodSession session)
    {
        if (s_terminals.TryGetValue(session.Id, out var existing))
        {
            return existing;
        }

        var (shellFile, shellArgs, shellKind) = ResolveShell();

        var builder = Hex1bTerminal.CreateBuilder()
            .WithTerminalWidget(out var handle)
            .WithPtyProcess(opts =>
            {
                opts.FileName = shellFile;
                opts.Arguments ??= new List<string>();
                foreach (var a in shellArgs)
                {
                    opts.Arguments.Add(a);
                }
                // Intentionally do NOT set opts.Environment here. The
                // dogfooding overrides are applied by typing shell commands
                // after launch so they are visible in the terminal scrollback.
                // The child inherits the parent's environment via
                // InheritEnvironment=true (default).
            });

        var terminal = builder.Build();
        session.Status = SessionStatus.Running;

        // Drive the embedded terminal lifecycle on a background task; the
        // render path must remain synchronous. The automator runs as a sibling
        // task once the terminal is alive so its typing shows up in the PTY.
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
            _ = Task.Run(() => ApplyOverridesAsync(terminal, shellKind, plan, cts.Token), cts.Token);
        }

        var record = new EmbeddedTerminal(terminal, handle, cts);
        s_terminals[session.Id] = record;
        return record;
    }

    private static async Task ApplyOverridesAsync(
        Hex1bTerminal terminal,
        ShellKind shellKind,
        SessionEnvironmentPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            // Default step timeout — generous because the shell may still be
            // sourcing its profile when we start typing.
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            // Wait for the shell to reach a usable state before typing. We
            // can't rely on a single prompt regex across bash/zsh/pwsh/cmd
            // (PS1 customisations defeat anything specific), so we use a
            // short fixed delay. The user sees the commands appear once the
            // prompt is rendered; if the shell is still booting, the input
            // is queued by the PTY and runs at the first prompt anyway.
            await automator.WaitAsync(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);

            foreach (var line in FormatCommands(shellKind, plan))
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

    private static IEnumerable<string> FormatCommands(ShellKind shell, SessionEnvironmentPlan plan)
    {
        switch (shell)
        {
            case ShellKind.Pwsh:
                yield return "Write-Host '# aspire-dogfooder: applying identity overrides' -ForegroundColor Cyan";
                if (plan.PathPrependDir is { Length: > 0 } pwshPath)
                {
                    yield return $"$env:PATH = '{EscapePwsh(pwshPath)}' + [IO.Path]::PathSeparator + $env:PATH";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"$env:{k} = '{EscapePwsh(v)}'";
                }
                yield return "Write-Host '# done. dogfooding session ready.' -ForegroundColor Cyan";
                break;

            case ShellKind.Cmd:
                yield return "echo # aspire-dogfooder: applying identity overrides";
                if (plan.PathPrependDir is { Length: > 0 } cmdPath)
                {
                    yield return $"set PATH={cmdPath};%PATH%";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"set {k}={v}";
                }
                yield return "echo # done. dogfooding session ready.";
                break;

            default:
                // bash / zsh / sh / other POSIX-flavoured shells.
                yield return "echo '# aspire-dogfooder: applying identity overrides'";
                if (plan.PathPrependDir is { Length: > 0 } posixPath)
                {
                    yield return $"export PATH=\"{EscapePosix(posixPath)}:$PATH\"";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"export {k}=\"{EscapePosix(v)}\"";
                }
                yield return "echo '# done. dogfooding session ready.'";
                break;
        }
    }

    // Within a POSIX double-quoted string we only have to escape: $ ` " \
    // Backslash MUST be replaced first to avoid double-escaping the others.
    private static string EscapePosix(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    // Within a PowerShell single-quoted string, single quotes are the only
    // escape concern (doubled to literalise). No $ expansion happens inside ''.
    private static string EscapePwsh(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static (string FileName, IReadOnlyList<string> Args, ShellKind Kind) ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pwsh = Environment.GetEnvironmentVariable("DOGFOODER_SHELL") ?? "pwsh.exe";
            return (pwsh, Array.Empty<string>(), DetectKind(pwsh));
        }

        var shell = Environment.GetEnvironmentVariable("DOGFOODER_SHELL")
            ?? Environment.GetEnvironmentVariable("SHELL")
            ?? "/bin/bash";

        // --norc on bash keeps the launched shell from picking up the user's
        // interactive customisations; the goal is a deterministic identity
        // override session, not a personalised shell. Other shells just get
        // launched with no extra args.
        var kind = DetectKind(shell);
        var args = kind == ShellKind.Bash ? new[] { "--norc" } : Array.Empty<string>();
        return (shell, args, kind);
    }

    private static ShellKind DetectKind(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name switch
        {
            "bash" or "sh" => ShellKind.Bash,
            "zsh" => ShellKind.Zsh,
            "pwsh" or "powershell" => ShellKind.Pwsh,
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Bash, // most plausible default on Unix-likes.
        };
    }

    private enum ShellKind { Bash, Zsh, Pwsh, Cmd }

    private sealed record EmbeddedTerminal(
        Hex1bTerminal Terminal,
        TerminalWidgetHandle Handle,
        CancellationTokenSource Cts);
}
