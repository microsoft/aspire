// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate Aspire CLI starter templates can be created and started
/// on Windows. Uses the interactive <c>aspire new</c> flow (same as the Docker-based tests)
/// so no <c>--channel</c> flag or pre-installed CLI is needed.
/// </summary>
public sealed class StarterValidationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CSharpStarter_NewStartStop()
    {
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(300));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (CliE2ETestHelpers.IsRunningInCI)
        {
            var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
            var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
            await auto.InstallAspireBundleFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireBundleEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        output.WriteLine("Creating C# starter app...");

        // Use the interactive aspire new flow (same approach as Docker-based E2E tests)
        await auto.AspireNewAsync("StarterCsSmoke", counter, useRedisCache: false);

        output.WriteLine("Created. Navigating into project...");

        // Navigate into the project
        var cdCommand = OperatingSystem.IsWindows()
            ? "Set-Location StarterCsSmoke"
            : "cd StarterCsSmoke";
        await auto.TypeAsync(cdCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("Starting app...");

        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            snapshot => snapshot.GetScreenText().Contains("Apphost started successfully"),
            timeout: TimeSpan.FromMinutes(3),
            description: "aspire start to complete (Apphost started successfully)");
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("App started. Waiting for apiservice...");

        await auto.TypeAsync("aspire wait apiservice --status up --timeout 120");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        output.WriteLine("Resources are up. Stopping...");

        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        output.WriteLine("C# starter validation complete.");

        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
