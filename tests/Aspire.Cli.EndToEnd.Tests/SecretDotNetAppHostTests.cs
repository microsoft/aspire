// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Tests aspire secret CRUD operations on a .NET AppHost.
/// </summary>
public sealed class SecretDotNetAppHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SecretCrudOnDotNetAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Create an Empty AppHost project interactively
        await auto.AspireNewAsync("TestSecrets", counter, template: AspireTemplate.EmptyAppHost);

        // cd into the project
        await auto.TypeAsync("cd TestSecrets");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set secrets
        await auto.TypeAsync("aspire secret set Azure:Location eastus2");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set Parameters:db-password s3cret");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Get
        await auto.TypeAsync("aspire secret get Azure:Location");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("eastus2", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // List
        await auto.TypeAsync("aspire secret list");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("db-password", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Delete
        await auto.TypeAsync("aspire secret delete Azure:Location");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("deleted successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify deletion
        await auto.TypeAsync("aspire secret list");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("db-password", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
