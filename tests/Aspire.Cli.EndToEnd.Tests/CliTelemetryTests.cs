// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test that verifies telemetry enrichment tags are present on spans
/// exported via the profiling capture pipeline. Uses <c>aspire start --capture-profile</c>
/// which activates the profiling TracerProvider, creates CLI profiling spans enriched by
/// CliTagEnrichmentProcessor, and exports them to a
/// profile archive file that we can inspect.
/// </summary>
public sealed class CliTelemetryTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task OtlpExportedSpansContainEnrichmentTags()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        // Docker socket needed because aspire start runs an AppHost that may launch containers
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a project so aspire start has an AppHost to launch
        await auto.AspireNewAsync("TelemetryTestApp", counter);

        // Navigate to the AppHost
        await auto.TypeAsync("cd TelemetryTestApp/TelemetryTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost with --capture-profile to generate CLI profiling spans.
        // --capture-profile activates the profiling TracerProvider which creates spans on
        // the Aspire.Cli.Profiling activity source. The CliTagEnrichmentProcessor enriches
        // these spans with default tags (aspire.cli.version, etc.) before export.
        // The profile is exported to a ZIP archive containing OTLP-format trace data.
        // Write diagnostic files into the .aspire-diagnostics/ directory so TerminalRun
        // always copies them to testresults (not just on failure).
        var diagDir = $"$ASPIRE_E2E_WORKSPACE/{CliE2EAutomatorHelpers.DiagnosticsDirectoryName}";
        var profilePath = $"{diagDir}/profile.aspire";
        var verResultPath = $"{diagDir}/ver_result";
        await auto.RunCommandAsync($"mkdir -p {diagDir}", counter);
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(4), additionalArgs: $"--capture-profile --capture-profile-output {profilePath}");

        // Stop the background AppHost
        await auto.AspireStopAsync(counter);

        // Dump profile contents for debugging visibility in the recording
        await auto.TypeAsync($"unzip -l {profilePath}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Extract the trace data from the profile archive and check for enrichment tags.
        // The profile ZIP contains traces/profile.json in OTLP JSON format:
        //   { "resourceSpans": [{ "scopeSpans": [{ "spans": [{ "attributes": [{ "key": "...", "value": {...} }] }] }] }] }
        // Some spans may have no attributes field (omitted by WhenWritingNull), so use (.attributes // [])
        // to avoid jq errors on null iteration.
        await auto.TypeAsync($"unzip -p {profilePath} traces/profile.json | jq -e '[.resourceSpans[].scopeSpans[].spans[] | (.attributes // [])[] | select(.key == \"aspire.cli.version\")] | length > 0' >/dev/null 2>&1 && echo PASS > {verResultPath} || echo FAIL > {verResultPath}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Dump some span data for debugging
        await auto.TypeAsync($"unzip -p {profilePath} traces/profile.json | jq '.resourceSpans[].scopeSpans[].spans[] | {{name, attributes: [(.attributes // [])[] | select(.key | startswith(\"aspire.cli\"))]}}' | head -50");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Clear and check the enrichment tag result
        await auto.TypeAsync("clear");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"cat {verResultPath}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("PASS", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
