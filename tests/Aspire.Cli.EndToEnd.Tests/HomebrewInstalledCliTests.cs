// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that exercise a Homebrew-installed Aspire CLI against a real host terminal.
/// </summary>
/// <remarks>
/// Unlike the rest of this suite, these run in the <b>non-Docker</b> host terminal
/// (<see cref="CliE2ETestHelpers.CreateTestTerminal"/>): the brew prefix lives on the runner host,
/// not in the Linux e2e container, and only the host path can cover osx-arm64. The brew install
/// itself is a pre-step (CI job or local) — the harness only puts brew's bin on PATH via
/// <see cref="CliInstallMode.Homebrew"/>.
///
/// Every test no-ops (via <see cref="Assert.Skip(string)"/>) unless the install strategy resolves
/// to Homebrew (<c>ASPIRE_E2E_HOMEBREW=true</c>), so the class is inert in the regular Docker suite.
///
/// These cover the brew route beyond the formula's non-interactive smoke (which already checks
/// <c>--version</c>, <c>doctor</c> route, the <c>update --self</c> gate, and <c>new aspire-empty</c>):
/// the interactive lifecycle, describe, config round-trips, and the gate as a user sees it.
/// </remarks>
public sealed class HomebrewInstalledCliTests(ITestOutputHelper output)
{
    private const string SkipReason =
        "HomebrewInstalledCliTests only runs against a brew-installed CLI (set ASPIRE_E2E_HOMEBREW=true after 'brew install aspire').";

    /// <summary>
    /// Full interactive lifecycle on a brew-installed CLI: create a starter app, start it,
    /// confirm it shows up in <c>aspire ps</c>, confirm its resources via <c>aspire describe</c>,
    /// then stop it. Needs a .NET SDK on PATH and NuGet access (the starter app restores
    /// Aspire packages from the channel baked into the formula).
    /// </summary>
    [Fact]
    public async Task NewStartPsDescribeStop_OnBrewInstalledCli()
    {
        var strategy = DetectHomebrewOrSkip();

        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareEnvironmentAsync(workspace, counter);
        await auto.InstallAspireCliInShellAsync(strategy, counter);

        const string projectName = "BrewLifecycleApp";
        await auto.AspireNewAsync(projectName, counter);

        await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);

        // ps before start: nothing running.
        await auto.TypeAsync("aspire ps");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(SharedCommandStrings.AppHostNotRunning, timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Start detached.
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // ps now lists the running AppHost.
        await auto.TypeAsync("aspire ps");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync($"{projectName}.AppHost", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Give the resources a moment to surface, then describe them.
        await auto.RunCommandAsync("sleep 5", counter);
        await auto.TypeAsync("aspire describe");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("apiservice", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop.
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// <c>aspire config set</c>/<c>get</c> round-trips on a brew-installed CLI. Pure CLI behavior:
    /// no apphost build, no .NET SDK, no network.
    /// </summary>
    [Fact]
    public async Task ConfigSetAndGet_RoundtripsOnBrewInstalledCli()
    {
        var strategy = DetectHomebrewOrSkip();

        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(120));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareEnvironmentAsync(workspace, counter);
        await auto.InstallAspireCliInShellAsync(strategy, counter);

        // Workspace-scoped config (no -g) writes aspire.config.json in the cwd, so this does
        // not mutate the developer's global config when run locally. The set→get round-trip is
        // the meaningful check that config read/write works on the brew binary.
        await auto.RunCommandAsync("aspire config set features.testBrewFlag true", counter);

        await auto.TypeAsync("aspire config get features.testBrewFlag");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("true", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// <c>aspire doctor</c> reports the brew install route for the running CLI. Mirrors the formula's
    /// non-interactive install-job check, but through the interactive terminal a user actually sees.
    /// </summary>
    [Fact]
    public async Task Doctor_ReportsBrewInstallRouteOnBrewInstalledCli()
    {
        var strategy = DetectHomebrewOrSkip();

        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(120));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareEnvironmentAsync(workspace, counter);
        await auto.InstallAspireCliInShellAsync(strategy, counter);

        // doctor can exit non-zero on host-check failures (e.g. no running Docker), which we
        // don't gate on. Assert only the install-route via --format json: an installation whose
        // route is "brew" and whose pathStatus is "active" (i.e. the running CLI). The human
        // table renders "(current)" on a separate line from the brew row, so a single-line text
        // match is unreliable — parse the structured output instead.
        //
        // RunCommandAsync asserts the command's exit code: the python check exits 0 only when a
        // brew+active installation exists, so this single call IS the assertion (the trailing
        // echo is just for log readability).
        await auto.RunCommandAsync(
            "aspire doctor --format json > doctor.json 2>/dev/null || true; " +
            "python3 -c \"import json; d=json.load(open('doctor.json')); " +
            "exit(0 if any(i.get('route')=='brew' and i.get('pathStatus')=='active' for i in d.get('installations',[])) else 1)\" " +
            "&& echo DOCTOR_BREW_ROUTE_OK",
            counter,
            TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// <c>aspire update --self</c> on a brew-installed CLI prints the <c>brew upgrade aspire</c>
    /// guidance and exits 0 instead of overwriting the Cellar-owned binary.
    /// </summary>
    [Fact]
    public async Task UpdateSelf_PrintsBrewUpgradeGuidanceOnBrewInstalledCli()
    {
        var strategy = DetectHomebrewOrSkip();

        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(120));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareEnvironmentAsync(workspace, counter);
        await auto.InstallAspireCliInShellAsync(strategy, counter);

        // The gate must print "brew upgrade aspire" AND exit 0. Capture both so a non-zero exit
        // (e.g. if the GitHub downloader were wrongly invoked) fails the prompt-counter assertion.
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("brew upgrade aspire", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    private CliInstallStrategy DetectHomebrewOrSkip()
    {
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode is not CliInstallMode.Homebrew)
        {
            Assert.Skip(SkipReason);
        }

        return strategy;
    }
}
