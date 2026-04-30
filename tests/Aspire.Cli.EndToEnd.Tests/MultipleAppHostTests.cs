// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Tests that <c>aspire start --format json</c> produces well-formed JSON
/// without human-readable messages polluting stdout.
/// </summary>
public sealed class MultipleAppHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DetachFormatJsonProducesValidJson()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a single project using aspire new
        await auto.AspireNewAsync("TestApp", counter);

        await auto.ClearScreenAsync(counter);

        // Navigate into the project directory
        await auto.TypeAsync("cd TestApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // First: launch the apphost with --detach (interactive, no JSON)
        // Just wait for the command to complete (WaitForSuccessPrompt waits for the shell prompt)
        await auto.AspireStartAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Second: launch again with --detach --format json, redirecting stdout to a file.
        // This tests that stdout remains a single well-formed JSON document.
        // stderr is left visible in the terminal for debugging if the command fails.
        await auto.TypeAsync("aspire start --format json > output.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Validate the JSON output file is well-formed by using python to parse it.
        await auto.TypeAsync("python3 -c \"import json; data = json.load(open('output.json')); assert 'appHostPath' in data; assert 'appHostPid' in data; print('JSON_VALID')\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("JSON_VALID", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Also cat the file so we can see it in the recording
        await auto.TypeAsync("cat output.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: stop any running instances
        await auto.AspireStopAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task DetachFormatJsonProducesValidJsonWhenRestartingExistingInstance()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("RestartTestApp", counter);

        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync("cd RestartTestApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Capture stdout so the file remains a single JSON document even when the restart path
        // emits human-readable progress messages to stderr.
        await auto.TypeAsync("aspire start --format json > output.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync("python3 -c \"import json; data = json.load(open('output.json')); assert 'appHostPath' in data; assert 'appHostPid' in data; print('JSON_VALID')\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("JSON_VALID", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire stop --all 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
