// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Screens;
using Aspire.Dogfooder.Screens.Workspace;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Commands;

/// <summary>
/// The default action — boots the interactive Hex1b TUI. Owns the lifetime
/// of the root <see cref="Hex1bApp"/>, the embedded <see cref="Hex1bTerminal"/>,
/// and the cancellation plumbing that ties them to the host's
/// <c>ApplicationStopping</c> token.
/// </summary>
internal sealed class RunCommand
{
    public RunCommand(
        AppState state,
        IGitHubAuthProbe ghProbe,
        IDogfoodSessionPreparer preparer,
        ILocalAspireCliLocator cliLocator,
        Scenarios.DogfoodScenarioRegistry scenarioRegistry)
    {
        _state = state;
        _ghProbe = ghProbe;
        _preparer = preparer;
        _cliLocator = cliLocator;
        _workspace = new WorkspaceScreen(state, preparer, scenarioRegistry);
    }

    private readonly AppState _state;
    private readonly IGitHubAuthProbe _ghProbe;
    private readonly IDogfoodSessionPreparer _preparer;
    private readonly ILocalAspireCliLocator _cliLocator;
    private readonly WorkspaceScreen _workspace;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Load any previously-persisted sessions before showing the UI so the
        // session list isn't empty on second-launch.
        await _state.Sessions.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Run validation probes up-front in the background. The validation
        // screen subscribes to ChangeNotifier so its widget tree re-renders
        // as each probe result lands.
        _ = Task.Run(() => RunValidationProbesAsync(cancellationToken), cancellationToken);

        Hex1bApp? capturedApp = null;

        await using var workspaceDisposables = _workspace.Disposables;

        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithMouse(true)
            .WithHex1bApp((app, _) =>
            {
                capturedApp = app;
                // Bridge state-change events into Hex1b's render loop. We only
                // subscribe once; the lambda holds a closure over `app` which
                // outlives the widget builder.
                _state.Notifier.Changed += () => app.Invalidate();
                return ctx => BuildRoot(ctx);
            })
            .Build();

        await terminal.RunAsync(cancellationToken).ConfigureAwait(false);

        // Best-effort save on shutdown. Failures here shouldn't crash the
        // process — they'll resurface on next launch when a fresh save runs.
        try
        {
            await _state.Sessions.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: shutdown path.
        }

        _ = capturedApp;
        return 0;
    }

    private Hex1bWidget BuildRoot(RootContext ctx) =>
        _state.Phase switch
        {
            AppPhase.Validation => EnvironmentValidationScreen.Build(ctx, _state),
            AppPhase.Main => _workspace.Build(ctx),
            _ => ctx.Text("Unknown phase."),
        };

    private async Task RunValidationProbesAsync(CancellationToken cancellationToken)
    {
        var v = _state.Validation;

        // dotnet probe — we just check that `dotnet --version` runs. The
        // command's exit code is enough; the version string is shown to the
        // user as confirmation.
        v.UpdateDotnet(EnvironmentProbeResult.Running("dotnet"));
        try
        {
            var dotnetVersion = await TryRunCaptureAsync("dotnet", ["--version"], cancellationToken).ConfigureAwait(false);
            v.UpdateDotnet(dotnetVersion is null
                ? EnvironmentProbeResult.Failed("dotnet", "`dotnet --version` failed. Install the .NET SDK and retry.")
                : EnvironmentProbeResult.Ok("dotnet", $"SDK {dotnetVersion.Trim()}"));
        }
        catch (Exception ex)
        {
            v.UpdateDotnet(EnvironmentProbeResult.Failed("dotnet", $"Probe failed: {ex.Message}"));
        }

        // gh auth probe.
        v.UpdateGhAuth(EnvironmentProbeResult.Running("gh auth"));
        var auth = await _ghProbe.CheckAuthAsync(cancellationToken).ConfigureAwait(false);
        v.UpdateGhAuth(auth.IsAuthenticated
            ? EnvironmentProbeResult.Ok("gh auth", auth.Detail)
            : EnvironmentProbeResult.Failed("gh auth", auth.Detail));

        // gh token probe — only meaningful if auth succeeded.
        if (auth.IsAuthenticated)
        {
            v.UpdateGhToken(EnvironmentProbeResult.Running("gh token"), null);
            var token = await _ghProbe.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            v.UpdateGhToken(token is null
                ? EnvironmentProbeResult.Failed("gh token", "`gh auth token` returned no token.")
                : EnvironmentProbeResult.Ok("gh token", $"Cached ({token.Length} chars)."),
                token);
        }
        else
        {
            v.UpdateGhToken(EnvironmentProbeResult.Failed("gh token", "Skipped — gh auth not OK."), null);
        }

        // Local CLI probe — failure here is not fatal; the embedded shell
        // simply won't have artifacts/bin/Aspire.Cli prepended to PATH and
        // `aspire` will resolve to the global install. Surface a clear
        // remediation hint so the user knows to run ./build.sh.
        if (_cliLocator.CliExecutablePath is { Length: > 0 } cliPath)
        {
            v.UpdateLocalCli(EnvironmentProbeResult.Ok("local cli", cliPath));
        }
        else
        {
            v.UpdateLocalCli(EnvironmentProbeResult.Failed(
                "local cli",
                "No artifacts/bin/Aspire.Cli/**/aspire found. Run ./build.sh first."));
        }
    }

    private static async Task<string?> TryRunCaptureAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
