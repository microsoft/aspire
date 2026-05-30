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
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create TypeScript AppHost via aspire init
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("> C#", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Set secrets using --apphost
        await auto.TypeAsync("aspire secret set MyConfig:ApiKey test-key-123 --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set ConnectionStrings:Db Server=localhost --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Get
        await auto.TypeAsync("aspire secret get MyConfig:ApiKey --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("test-key-123", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // List
        await auto.TypeAsync("aspire secret list --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("ConnectionStrings:Db", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Delete
        await auto.TypeAsync("aspire secret delete MyConfig:ApiKey --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("deleted successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify deletion
        await auto.TypeAsync("aspire secret list --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("ConnectionStrings:Db", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set MyConfig:ApiKey staging-ts-secret --environment Staging --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set MyConfig:ApiKey production-ts-secret --environment Production --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret get MyConfig:ApiKey --environment Staging --apphost apphost.mts | grep -qx 'staging-ts-secret' && echo E2E_TS_STAGING_SECRET_GET_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_TS_STAGING_SECRET_GET_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret get MyConfig:ApiKey --environment Production --apphost apphost.mts | grep -qx 'production-ts-secret' && echo E2E_TS_PRODUCTION_SECRET_GET_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_TS_PRODUCTION_SECRET_GET_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret list --all --apphost apphost.mts | grep 'staging-ts-secret' | grep 'Staging' >/dev/null && echo E2E_TS_SECRET_LIST_STAGING_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_TS_SECRET_LIST_STAGING_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret list --all --apphost apphost.mts | grep 'production-ts-secret' | grep 'Production' >/dev/null && echo E2E_TS_SECRET_LIST_PRODUCTION_OK");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("E2E_TS_SECRET_LIST_PRODUCTION_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
