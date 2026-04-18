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
/// End-to-end smoke tests for the core Aspire CLI template scenarios.
/// </summary>
public sealed class SmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunAspireStarterProject()
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

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireStarterApp", counter, useRedisCache: false);

        // Run the project with aspire run
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();

        // Regression test for https://github.com/microsoft/aspire/issues/13971
        // If the apphost selection prompt appears, it means multiple apphosts were
        // incorrectly detected (e.g., AppHost.cs was incorrectly treated as a single-file apphost)
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException(
                    "Unexpected apphost selection prompt detected! " +
                    "This indicates multiple apphosts were incorrectly detected.");
            }
            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.")
                || s.ContainsText("Press CTRL+C to stop the apphost and exit.");
        }, timeout: TimeSpan.FromMinutes(5), description: "Press CTRL+C message (aspire run started)");

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

    /// <summary>
    /// Creates a starter project, starts it with aspire start, and verifies the
    /// starter resources come up and the web frontend endpoint responds with HTTP 200.
    /// Catches runtime regressions where templates build but fail to serve traffic.
    /// </summary>
    [Fact]
    public async Task StarterTemplateEndpointsRespond()
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

        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AssertResourcesExistAsync(counter, "webfrontend", "apiservice");

        await auto.TypeAsync("aspire describe webfrontend --format json > webfrontend.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("WEB_URL=$(grep -oE 'https?://localhost:[0-9]+' webfrontend.json | head -1); echo \"$WEB_URL\"");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("http://localhost:") || s.ContainsText("https://localhost:"),
            timeout: TimeSpan.FromSeconds(30),
            description: "web frontend URL");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL -o /dev/null -w 'webfrontend-http-%{http_code}' \"$WEB_URL\" || echo 'webfrontend-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend-http-200", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

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
    public async Task CreateAndRunTypeScriptStarterProject()
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

        await auto.AspireNewAsync("TsStarterApp", counter, template: AspireTemplate.ExpressReact);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "TsStarterApp");
        GitIgnoreAssertions.AssertContainsEntry(projectRoot, ".aspire/");
        var modulesDir = Path.Combine(projectRoot, ".modules");

        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var aspireModulePath = Path.Combine(modulesDir, "aspire.ts");
        if (!File.Exists(aspireModulePath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireModulePath}");
        }

        await auto.TypeAsync("cd TsStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandFailFastAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunPythonReactProject()
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

        await auto.AspireNewAsync("AspirePyReactApp", counter, template: AspireTemplate.PythonReact, useRedisCache: false);

        GitIgnoreAssertions.AssertContainsEntry(
            Path.Combine(workspace.WorkspaceRoot.FullName, "AspirePyReactApp"),
            ".aspire/");

        await auto.TypeAsync("cd AspirePyReactApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandFailFastAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

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

        await auto.AspireNewAsync("BundleStarterApp", counter, useRedisCache: false);

        await auto.TypeAsync("aspire start --format json | tee /tmp/aspire-detach.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        await auto.TypeAsync("DASHBOARD_URL=$(sed -n 's/.*\"dashboardUrl\"[[:space:]]*:[[:space:]]*\"\\(https:\\/\\/localhost:[0-9]*\\).*/\\1/p' /tmp/aspire-detach.json | head -1)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL -o /dev/null -w 'dashboard-http-%{http_code}' \"$DASHBOARD_URL\" || echo 'dashboard-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("dashboard-http-200", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

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
