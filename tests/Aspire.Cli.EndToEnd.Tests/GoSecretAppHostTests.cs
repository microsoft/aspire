// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Tests aspire secret CRUD operations on a Go (polyglot) AppHost.
/// </summary>
public sealed class GoSecretAppHostTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task SecretCrudOnGoAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        // Create the Go AppHost via aspire init
        await auto.AspireInitGoAppHostAsync(counter);

        // Set secrets using --apphost
        await auto.TypeAsync("aspire secret set MyConfig:ApiKey test-key-123 --apphost apphost.go");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set ConnectionStrings:Db Server=localhost --apphost apphost.go");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Get and list are checked with grep because the secret keys and values also appear in the earlier
        // `secret set` command lines that are still on screen, so waiting for that text would pass without
        // any output. The numbered prompt reports grep's exit status, which is what validates the output.
        await auto.RunCommandAsync("aspire secret get MyConfig:ApiKey --apphost apphost.go | grep -qx 'test-key-123'", counter, TimeSpan.FromSeconds(30));
        await auto.RunCommandAsync("aspire secret list --apphost apphost.go | grep 'ConnectionStrings:Db' | grep 'Server=localhost' >/dev/null", counter, TimeSpan.FromSeconds(30));

        // Delete
        await auto.TypeAsync("aspire secret delete MyConfig:ApiKey --apphost apphost.go");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("deleted successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify deletion: waiting for screen text can only prove a secret is present, so list as JSON
        // (keyed by secret name) and assert the whole collection.
        await auto.RunCommandAsync("aspire secret list --apphost apphost.go --format json > listed-secrets.json", counter, TimeSpan.FromSeconds(30));
        var listedSecrets = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "listed-secrets.json"), TestContext.Current.CancellationToken));
        Assert.Equal(new Dictionary<string, string> { ["ConnectionStrings:Db"] = "Server=localhost" }, listedSecrets);

        await auto.TypeAsync("aspire secret set MyConfig:ApiKey staging-go-secret --environment Staging --apphost apphost.go");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire secret set MyConfig:ApiKey production-go-secret --environment Production --apphost apphost.go");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("set successfully", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandAsync("aspire secret get MyConfig:ApiKey --environment Staging --apphost apphost.go | grep -qx 'staging-go-secret'", counter, TimeSpan.FromSeconds(30));
        await auto.RunCommandAsync("aspire secret get MyConfig:ApiKey --environment Production --apphost apphost.go | grep -qx 'production-go-secret'", counter, TimeSpan.FromSeconds(30));
        await auto.RunCommandAsync("aspire secret list --all --apphost apphost.go | grep 'staging-go-secret' | grep 'Staging' >/dev/null", counter, TimeSpan.FromSeconds(30));
        await auto.RunCommandAsync("aspire secret list --all --apphost apphost.go | grep 'production-go-secret' | grep 'Production' >/dev/null", counter, TimeSpan.FromSeconds(30));
    }
}
