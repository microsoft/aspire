// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate Aspire CLI starter templates can be created and started.
/// These tests run natively on the host (no Docker) using the Hex1b PTY proxy on Windows
/// and bare bash on Linux. They replace the PowerShell-based cli-starter-validation.ps1 script.
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

        // Install CLI (GA release for local, PR build for CI)
        if (CliE2ETestHelpers.IsRunningInCI)
        {
            var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
        }

        await auto.SourceAspireCliEnvironmentAsync(counter);

        output.WriteLine("Creating C# starter app...");

        // aspire new with non-interactive mode
        await auto.TypeAsync("aspire new aspire-starter --name StarterCsSmoke --non-interactive --nologo");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        // Navigate into the project
        var cdCommand = OperatingSystem.IsWindows()
            ? "Set-Location StarterCsSmoke"
            : "cd StarterCsSmoke";
        await auto.TypeAsync(cdCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("Starting app...");

        // Start the app
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();

        // Wait for successful startup indication
        await auto.WaitUntilAsync(
            snapshot => snapshot.GetScreenText().Contains("Apphost started successfully"),
            timeout: TimeSpan.FromMinutes(3),
            description: "aspire start to complete (Apphost started successfully)");
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("App started. Waiting for resources...");

        // Wait for the apiservice resource to be up
        await auto.TypeAsync("aspire wait apiservice --status up --timeout 120");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        output.WriteLine("Resources are up. Stopping...");

        // Stop the app
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        output.WriteLine("C# starter validation complete.");

        // Exit cleanly
        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }

    [Fact]
    public async Task TypeScriptStarter_NewStartStop()
    {
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(300));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        // Install CLI (GA release for local, PR build for CI)
        if (CliE2ETestHelpers.IsRunningInCI)
        {
            var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
        }

        await auto.SourceAspireCliEnvironmentAsync(counter);

        output.WriteLine("Creating TypeScript starter app...");

        // aspire new with non-interactive mode
        await auto.TypeAsync("aspire new aspire-ts-starter --name StarterTsSmoke --non-interactive --nologo");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        // Navigate into the project
        var cdCommand = OperatingSystem.IsWindows()
            ? "Set-Location StarterTsSmoke"
            : "cd StarterTsSmoke";
        await auto.TypeAsync(cdCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("Starting app...");

        // Start the app
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            snapshot => snapshot.GetScreenText().Contains("Apphost started successfully"),
            timeout: TimeSpan.FromMinutes(3),
            description: "aspire start to complete (Apphost started successfully)");
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("App started. Waiting for resources...");

        // Wait for resources to be up
        await auto.TypeAsync("aspire wait app --status up --timeout 120");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        await auto.TypeAsync("aspire wait frontend --status up --timeout 120");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(3));

        output.WriteLine("Resources are up. Stopping...");

        // Stop the app
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        output.WriteLine("TypeScript starter validation complete.");

        // Exit cleanly
        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
