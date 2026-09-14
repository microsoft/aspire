// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI run command (creating and launching projects).
/// Each test class runs as a separate CI job for parallelization.
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

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireStarterApp", counter);

        // Run the project with aspire run. Use an explicit AppHost startup budget so a cold daily-feed
        // restore + build doesn't trip the CLI's default 120s timeout under CI contention.
        await auto.TypeAsync(CliE2EAutomatorHelpers.GetAspireRunCommand());
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
            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout, description: "Press CTRL+C message (aspire run started)");

        // Stop the running apphost with Ctrl+C
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task RedirectedRemoteSshRunUsesStaticOutput()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "RemoteSshRedirectedApp";
        await auto.AspireNewAsync(projectName, counter, useRedisCache: false);
        await auto.RunCommandAsync($"cd {projectName}", counter);

        // Playground mode normally forces interactive output and takes precedence over ambient CI
        // markers. Keeping it enabled makes redirected stdout the condition that selects static
        // rendering, so this test cannot silently pass because a new CI marker was inherited.
        var runCommand =
            "env " +
            "-u ASPIRE_NON_INTERACTIVE -u ASPIRE_ANSI_PASS_THRU " +
            "ASPIRE_PLAYGROUND=true " +
            "TERM=dumb LINES=0 COLUMNS=80 " +
            "VSCODE_IPC_HOOK_CLI=/tmp/vscode-ipc-remote-ssh " +
            "SSH_CONNECTION='127.0.0.1 12345 127.0.0.1 22' " +
            $"ASPIRE_CLI_START_TIMEOUT={CliE2EAutomatorHelpers.AspireRunStartupBudgetSeconds} " +
            "aspire run > remote-ssh.stdout 2> remote-ssh.stderr & echo $! > remote-ssh.pid";
        await auto.RunCommandAsync(runCommand, counter);

        // Static resource updates are emitted once as lines such as:
        //   Endpoints: worker has endpoint http://localhost:5830
        // Wait for multiple updates so a cumulative-snapshot fallback would be observable.
        await auto.RunCommandAsync(
            $"endpoint_count=0; cli_alive=1; " +
            $"for attempt in $(seq 1 {CliE2EAutomatorHelpers.AspireRunReadyTimeout.TotalSeconds}); do " +
            "endpoint_count=$(grep -Fc 'has endpoint' remote-ssh.stdout || true); " +
            "if [ \"$endpoint_count\" -ge 2 ]; then break; fi; " +
            "if ! kill -0 \"$(cat remote-ssh.pid)\" 2>/dev/null; then cli_alive=0; break; fi; " +
            "sleep 1; " +
            "done; " +
            "if [ \"$endpoint_count\" -ge 2 ]; then true; " +
            "else " +
            "if [ \"$cli_alive\" -eq 0 ]; then echo 'aspire run exited before two endpoint updates' >&2; " +
            "else echo 'timed out waiting for two endpoint updates' >&2; fi; " +
            "echo '--- remote-ssh.stdout ---' >&2; cat remote-ssh.stdout >&2; " +
            "echo '--- remote-ssh.stderr ---' >&2; cat remote-ssh.stderr >&2; false; " +
            "fi",
            counter,
            CliE2EAutomatorHelpers.AspireRunReadyTimeout + TimeSpan.FromSeconds(30));

        await auto.RunCommandAsync("kill -0 \"$(cat remote-ssh.pid)\"", counter);
        await auto.RunCommandAsync("aspire ps --format json > remote-ssh-ps.json", counter);
        await auto.RunCommandAsync(
            $"grep -Fq '{projectName}' remote-ssh-ps.json && grep -Fq '\"status\": \"running\"' remote-ssh-ps.json",
            counter);
        await auto.RunCommandAsync(
            "test -f remote-ssh.stdout && test -f remote-ssh.stderr && " +
            "! grep -F -e 'LiveRenderable' -e 'System.ArgumentException' " +
            "-e 'An unexpected error occurred' remote-ssh.stdout remote-ssh.stderr",
            counter);
        await auto.RunCommandAsync(
            "test \"$(tr -cd '\\r' < remote-ssh.stdout | wc -c)\" -eq 0",
            counter);
        await auto.RunCommandAsync(
            "test -z \"$(grep -F 'has endpoint' remote-ssh.stdout | sort | uniq -d)\" && " +
            "test \"$(grep -Foc 'CTRL+C' remote-ssh.stdout)\" -eq 1",
            counter);
        await auto.RunCommandAsync(
            "cli_pid=$(cat remote-ssh.pid); kill -INT \"$cli_pid\"; wait \"$cli_pid\"; " +
            "cli_exit=$?; test \"$cli_exit\" -eq 0 && ! kill -0 \"$cli_pid\" 2>/dev/null",
            counter,
            TimeSpan.FromMinutes(1));
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunPolyglotAppHostWithDevLocalhostUrls()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "PolyglotDevLocalhost";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact, useDevLocalhost: true);

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.RunCommandAsync("grep -F 'ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL' aspire.config.json && grep -F 'polyglotdevlocalhost.dev.localhost' aspire.config.json", counter);

        await auto.TypeAsync(CliE2EAutomatorHelpers.GetAspireRunCommand());
        await auto.EnterAsync();

        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Capability Error") ||
                s.ContainsText("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL must contain a local loopback address"))
            {
                throw new InvalidOperationException("Polyglot AppHost failed to start with a *.dev.localhost resource service endpoint.");
            }

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout, description: "Press CTRL+C message for polyglot AppHost with *.dev.localhost URLs");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task LatestCliCanStartStableChannelAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

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

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task LatestCliCanStartStableChannelTypeScriptAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "StableTypeScriptAppHost";
        await auto.AspireNewTypeScriptEmptyAppHostAsync(projectName, counter, channel: "stable");

        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var configPath = Path.Combine(projectPath, "aspire.config.json");
        var appHostFileName = GetStableTypeScriptAppHostFileName(configPath);
        var appHostPath = Path.Combine(projectPath, appHostFileName);
        if (!File.Exists(appHostPath))
        {
            throw new FileNotFoundException($"Expected TypeScript AppHost file to exist: {appHostPath}", appHostPath);
        }

        output.WriteLine("Stable TypeScript AppHost config verified.");

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
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

    private static string GetStableTypeScriptAppHostFileName(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected Aspire config file to exist: {configPath}", configPath);
        }

        // Stable channel can lag behind current TypeScript template naming, so the
        // AppHost path is expected to match whichever stable template was created.
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
        var appHostPath = GetRequiredJsonStringProperty(appHost, "path", configPath);
        if (appHostPath is not ("apphost.mts" or "apphost.ts"))
        {
            throw new InvalidOperationException($"Expected JSON property 'path' in {configPath} to be 'apphost.mts' or 'apphost.ts', got '{appHostPath}'.");
        }

        AssertJsonStringProperty(appHost, "language", "typescript/nodejs", configPath);

        return appHostPath;
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
