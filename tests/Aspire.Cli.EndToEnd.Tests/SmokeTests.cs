// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke tests for the core Aspire CLI starter scenarios and JSON contracts.
/// </summary>
public sealed class SmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunAspireStarterProjectWithBundle()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("BundleStarterApp", counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    [Trait("azdo-template-coverage", "true")]
    public async Task CreateAndRunDefaultAspireStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new with the default starter options.
        await auto.AspireNewAsync("AspireStarterApp", counter);

        // Run the project with aspire run and persist the transcript for failed-run debugging.
        await auto.AspireRunUntilReadyAsync(workspace);

        // Stop the running apphost with Ctrl+C
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task LatestCliCanStartStableChannelAppHost()
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

        const string projectName = "StableAppHost";
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter, channel: "stable");

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
        var appHostSdkVersion = GetAppHostSdkVersion(appHostPath);
        if (appHostSdkVersion.Contains('-', StringComparison.Ordinal) ||
            appHostSdkVersion.Contains('+', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected stable Aspire.AppHost.Sdk version, got '{appHostSdkVersion}' in {appHostPath}.");
        }

        output.WriteLine($"Stable AppHost SDK version: {appHostSdkVersion}");

        await auto.RunCommandFailFastAsync($"cd {projectName}", counter);
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task LatestCliCanStartStableChannelTypeScriptAppHost()
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

        const string projectName = "StableTypeScriptAppHost";
        await auto.AspireNewTypeScriptEmptyAppHostAsync(projectName, counter, channel: "stable");

        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostPath = Path.Combine(projectPath, "apphost.ts");
        if (!File.Exists(appHostPath))
        {
            throw new FileNotFoundException($"Expected TypeScript AppHost file to exist: {appHostPath}", appHostPath);
        }

        AssertStableTypeScriptAppHostConfig(Path.Combine(projectPath, "aspire.config.json"));
        output.WriteLine("Stable TypeScript AppHost config verified.");

        await auto.RunCommandFailFastAsync($"cd {projectName}", counter);
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("AspireEmptyApp", counter, template: AspireTemplate.EmptyAppHost);

        await auto.TypeAsync("cd AspireEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    [Trait("azdo-template-coverage", "true")]
    public async Task CreateAndRunDotNetEmptyTemplateProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableShowAllTemplatesAsync(counter);

        await auto.AspireNewSubcommandAsync("aspire", "DotNetEmptyApp", counter, "--localhost-tld", "false");

        await auto.TypeAsync("cd DotNetEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    [Trait("azdo-template-coverage", "true")]
    public async Task CreateAndRunDotNetAppHostTemplateProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableShowAllTemplatesAsync(counter);

        await auto.AspireNewSubcommandAsync("aspire-apphost", "DotNetAppHost", counter, "--localhost-tld", "false");

        await auto.TypeAsync("cd DotNetAppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Creates a starter project, starts it with <c>aspire start --format json</c>,
    /// validates machine-readable JSON contracts, and verifies the web frontend endpoint
    /// responds with HTTP 200.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task StarterJsonContractsAndEndpointsRespond()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("EndpointTest", counter, useRedisCache: false);

        await auto.TypeAsync("cd EndpointTest");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.PersistAspireStartJsonAsync(workspace, counter);
        CliE2ETestHelpers.AssertAspireStartJsonContract(workspace);

        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        var psJsonPath = await auto.CaptureJsonOutputAsync(
            "aspire ps --format json",
            workspace,
            counter,
            "ps.json");
        CliE2ETestHelpers.AssertPsJsonContract(psJsonPath);

        await auto.AssertResourcesExistAsync(counter, "webfrontend", "apiservice");

        var webfrontendJsonPath = await auto.CaptureJsonOutputAsync(
            "aspire describe webfrontend --format json",
            workspace,
            counter,
            "webfrontend.json");
        var webUrl = CliE2ETestHelpers.GetFirstLocalhostUrlFromJsonFile(webfrontendJsonPath);
        await auto.AssertUrlRespondsAsync(webUrl, "webfrontend", counter);

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    private static string GetAppHostSdkVersion(string appHostPath)
    {
        if (!File.Exists(appHostPath))
        {
            throw new FileNotFoundException($"Expected AppHost file to exist: {appHostPath}", appHostPath);
        }

        var appHostContent = File.ReadAllText(appHostPath);
        var match = Regex.Match(appHostContent, @"(?m)^#:\s*sdk\s+Aspire\.AppHost\.Sdk@(?<version>\S+)\s*$");
        return match.Success
            ? match.Groups["version"].Value
            : throw new InvalidOperationException($"Could not find Aspire.AppHost.Sdk directive in {appHostPath}.");
    }

    private static void AssertStableTypeScriptAppHostConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected Aspire config file to exist: {configPath}", configPath);
        }

        // Expected shape: { "appHost": { "path": "apphost.ts", "language": "typescript/nodejs" }, "sdk": { "version": "13.2.0" }, "channel": "stable" }
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = config.RootElement;
        AssertJsonStringProperty(root, "channel", "stable", configPath);
        var sdk = GetRequiredJsonObjectProperty(root, "sdk", configPath);
        var sdkVersion = GetRequiredJsonStringProperty(sdk, "version", configPath);
        if (sdkVersion.Contains('-', StringComparison.Ordinal) ||
            sdkVersion.Contains('+', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected stable Aspire SDK version, got '{sdkVersion}' in {configPath}.");
        }

        var appHost = GetRequiredJsonObjectProperty(root, "appHost", configPath);
        AssertJsonStringProperty(appHost, "path", "apphost.ts", configPath);
        AssertJsonStringProperty(appHost, "language", "typescript/nodejs", configPath);
    }

    private static JsonElement GetRequiredJsonObjectProperty(JsonElement element, string propertyName, string configPath)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected JSON object property '{propertyName}' in {configPath}.");
        }

        return property;
    }

    private static void AssertJsonStringProperty(JsonElement element, string propertyName, string expectedValue, string configPath)
    {
        var actualValue = GetRequiredJsonStringProperty(element, propertyName, configPath);
        if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected JSON property '{propertyName}' in {configPath} to be '{expectedValue}', got '{actualValue}'.");
        }
    }

    private static string GetRequiredJsonStringProperty(JsonElement element, string propertyName, string configPath)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Expected JSON string property '{propertyName}' in {configPath}.");
        }

        return property.GetString()
            ?? throw new InvalidOperationException($"Expected JSON string property '{propertyName}' in {configPath} to be non-null.");
    }
}
