// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Tests aspire secret CRUD operations on a TypeScript (polyglot) AppHost.
/// </summary>
public sealed class SecretTypeScriptAppHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SecretCrudOnTypeScriptAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create TypeScript AppHost via aspire init
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("> C#", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Set secrets using --apphost
        await auto.TypeAsync("aspire secret set MyConfig:ApiKey test-key-123 --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set ConnectionStrings:Db Server=localhost --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Get
        await auto.TypeAsync("aspire secret get MyConfig:ApiKey --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("test-key-123", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // List
        await auto.TypeAsync("aspire secret list --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("ConnectionStrings:Db", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Delete
        await auto.TypeAsync("aspire secret delete MyConfig:ApiKey --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("deleted successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify deletion
        await auto.TypeAsync("aspire secret list --apphost apphost.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("ConnectionStrings:Db", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
