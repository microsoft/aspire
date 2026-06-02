// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Panels;

/// <summary>
/// Embeds a Hex1b <see cref="Hex1bTerminal"/> in the right pane with the
/// session's identity-override env vars pre-applied to the child shell. The
/// terminal is built lazily once per session and cached on the session object
/// so re-rendering the panel doesn't tear down and re-spawn the shell.
/// </summary>
internal static class SessionTerminalPanel
{
    // Cache of session-id → (terminal, handle, lifetime cts). Lives at module
    // scope because the panel's Build is called every render and we must not
    // spawn a fresh PTY per frame. Disposal is best-effort on shutdown — the
    // OS reaps the child shell when Dogfooder exits.
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

        var env = session.PreparedEnvironment ?? new Dictionary<string, string>();

        var builder = Hex1bTerminal.CreateBuilder()
            .WithTerminalWidget(out var handle)
            .WithPtyProcess(opts =>
            {
                var (file, args) = ResolveShell();
                opts.FileName = file;
                opts.Arguments ??= new List<string>();
                foreach (var a in args)
                {
                    opts.Arguments.Add(a);
                }
                opts.Environment ??= new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in env)
                {
                    opts.Environment[k] = v;
                }
            });

        var terminal = builder.Build();
        session.Status = SessionStatus.Running;

        // Run the embedded terminal on a background task; its lifetime is
        // bounded by the outer Dogfooder process. We don't await this task
        // here because the render path must remain synchronous.
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
        });

        var record = new EmbeddedTerminal(terminal, handle, cts);
        s_terminals[session.Id] = record;
        return record;
    }

    private static (string FileName, IReadOnlyList<string> Args) ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Prefer pwsh when available; otherwise fall back to the OS-supplied
            // PowerShell or cmd. We don't probe filesystem here — the PTY layer
            // will surface a launch error in the embedded terminal if missing.
            var pwsh = Environment.GetEnvironmentVariable("DOGFOODER_SHELL") ?? "pwsh.exe";
            return (pwsh, Array.Empty<string>());
        }

        var shell = Environment.GetEnvironmentVariable("DOGFOODER_SHELL")
            ?? Environment.GetEnvironmentVariable("SHELL")
            ?? "/bin/bash";

        // --norc on bash keeps the launched shell from picking up the user's
        // interactive customisations; the goal is a deterministic identity
        // override session, not a personalised shell. Other shells just get
        // launched with no extra args.
        return shell.EndsWith("bash", StringComparison.Ordinal)
            ? (shell, new[] { "--norc" })
            : (shell, Array.Empty<string>());
    }

    private sealed record EmbeddedTerminal(
        Hex1bTerminal Terminal,
        TerminalWidgetHandle Handle,
        CancellationTokenSource Cts);
}
