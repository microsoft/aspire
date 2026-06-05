// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the --list-steps feature on the aspire do, publish, and deploy commands.
/// Verifies that the CLI can list pipeline steps without executing them, and that
/// invalid combinations (such as `aspire do --list-steps` without a step argument)
/// surface a friendly error instead of crashing.
/// </summary>
public sealed class ListStepsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DoPublishAndDeployListStepsWork()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new Aspire project
        await auto.AspireNewAsync("ListStepsApp", counter);

        // Navigate to the AppHost project
        await auto.TypeAsync("cd ListStepsApp/ListStepsApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // 1. Regression for https://github.com/microsoft/aspire/issues/17526:
        //    `aspire do --list-steps` with no step argument should surface a friendly error
        //    pointing at concrete examples rather than crashing with
        //    'Sequence contains more than one matching element'.
        await auto.TypeAsync("aspire do --list-steps");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Sequence contains more than one matching element"))
            {
                throw new InvalidOperationException(
                    "aspire do --list-steps regressed: pipeline executed and crashed instead of surfacing the friendly validation error.");
            }
            // Match short fragments that are unlikely to straddle a wrap boundary in a narrow
            // terminal. The full error message is a single long sentence, so asserting on the
            // raw URL (or any 30+ char run) is flaky because the screen buffer inserts wrap
            // newlines that defeat ContainsText's literal substring match.
            return s.ContainsText("required when using --list-steps")
                && s.ContainsText("aspire.dev/");
        }, timeout: TimeSpan.FromMinutes(2),
            description: "waiting for friendly error with example and docs link");

        // The validation error returns a non-zero exit code, but the shell prompt should come back.
        await auto.WaitForAnyPromptAsync(counter);

        // 2. `aspire do <step> --list-steps` lists pipeline steps for that step.
        await auto.TypeAsync("aspire do deploy --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire do deploy --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // 3. `aspire publish --list-steps` lists steps for the publish target.
        await auto.TypeAsync("aspire publish --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire publish --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // 4. `aspire deploy --list-steps` lists steps for the deploy target.
        await auto.TypeAsync("aspire deploy --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire deploy --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the terminal
    }
}
