// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml.Linq;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Extension methods for <see cref="Hex1bTerminalAutomator"/> providing Docker E2E test helpers.
/// These parallel the <see cref="Hex1b.Automation.Hex1bTerminalInputSequenceBuilder"/>-based methods in <see cref="CliE2ETestHelpers"/>.
/// </summary>
/// <remarks>
/// These helpers are intentionally bash-first and Linux-specific. The tests drive a real terminal session, so the
/// implementation keeps the shell commands visible instead of abstracting every step behind helper layers. That keeps
/// the code, the asciinema recording, and the failure output aligned when a scenario needs debugging.
/// </remarks>
internal static class CliE2EAutomatorHelpers
{
    private const string AspireStartJsonFile = "/tmp/aspire-start.json";
    private static readonly string s_expectedStableVersionMarker = GetExpectedStableVersionMarker();

    /// <summary>
    /// Prepares the Docker environment by setting up prompt counting, umask, and environment variables.
    /// </summary>
    internal static async Task PrepareDockerEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace? workspace = null,
        bool enableDcpDiagnostics = false)
    {
        // Wait for container to be ready (root prompt)
        await auto.WaitUntilTextAsync("# ", timeout: TimeSpan.FromSeconds(60));

        await auto.WaitAsync(500);

        // Install the numbered prompt contract used throughout these tests. The prompt encodes both the command
        // sequence number and the exit code so waits can synchronize on shell completion instead of timing guesses.
        const string promptSetup = "CMDCOUNT=0; PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1=\"[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \\$ \"'";
        await auto.TypeAsync(promptSetup);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set permissive umask
        await auto.TypeAsync("umask 000");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set environment variables
        await auto.TypeAsync("export ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        if (enableDcpDiagnostics)
        {
            await auto.TypeAsync("export DCP_DIAGNOSTICS_LOG_LEVEL=debug DCP_DIAGNOSTICS_LOG_FOLDER=~/.aspire/dcp-logs DCP_PRESERVE_EXECUTABLE_LOGS=1");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

        if (workspace is not null)
        {
            var containerWorkspace = $"/workspace/{workspace.WorkspaceRoot.Name}";

            await auto.TypeAsync($"cd {containerWorkspace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"export ASPIRE_E2E_WORKSPACE={containerWorkspace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            if (!CliE2ETestHelpers.IsRunningInCI && ShouldPreserveLocalWorkspace())
            {
                workspace.Preserve();
            }

            if (ShouldCaptureWorkspaceDiagnostics())
            {
                await auto.TypeAsync(
                    "trap 'if [ -n \"$ASPIRE_E2E_WORKSPACE\" ]; then " +
                    BuildAspireDiagnosticsCaptureCommand("$ASPIRE_E2E_WORKSPACE") +
                    "fi' EXIT");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);
            }
        }
    }

    /// <summary>
    /// Installs the Aspire CLI inside a Docker container based on the detected install mode.
    /// </summary>
    internal static async Task InstallAspireCliInDockerAsync(
        this Hex1bTerminalAutomator auto,
        CliE2ETestHelpers.DockerInstallMode installMode,
        SequenceCounter counter)
    {
        switch (installMode)
        {
            case CliE2ETestHelpers.DockerInstallMode.SourceBuild:
                await auto.TypeAsync("mkdir -p ~/.aspire/bin && cp /opt/aspire-cli/aspire ~/.aspire/bin/aspire && chmod +x ~/.aspire/bin/aspire");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
                await auto.TypeAsync("export PATH=~/.aspire/bin:$PATH");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);
                break;

            case CliE2ETestHelpers.DockerInstallMode.GaRelease:
                await auto.TypeAsync("/opt/aspire-scripts/get-aspire-cli.sh");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(120));
                await auto.TypeAsync("export PATH=~/.aspire/bin:$PATH");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);
                break;

            case CliE2ETestHelpers.DockerInstallMode.PullRequest:
                var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
                await auto.TypeAsync($"/opt/aspire-scripts/get-aspire-cli-pr.sh {GetPullRequestInstallArgs(prNumber)}");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(300));
                await auto.TypeAsync("export PATH=~/.aspire/bin:~/.aspire:$PATH");
                await auto.EnterAsync();
                await auto.WaitForSuccessPromptAsync(counter);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(installMode));
        }
    }

    /// <summary>
    /// Installs the Aspire CLI inside a Docker container using the given install strategy.
    /// Handles all modes: LocalHive, PullRequest, and InstallScript.
    /// </summary>
    internal static async Task InstallAspireCliAsync(
        this Hex1bTerminalAutomator auto,
        CliInstallStrategy strategy,
        SequenceCounter counter)
    {
        switch (strategy.Mode)
        {
            case CliInstallMode.LocalHive:
                await auto.ExtractLocalHiveArchiveAsync("/tmp/aspire-localhive.tar.gz", counter);
                await auto.RunCommandAsync("export PATH=~/.aspire/bin:$PATH", counter);
                await auto.ConfigureLocalHiveAsync(counter);
                break;

            case CliInstallMode.PullRequest:
                var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
                await auto.RunCommandFailFastAsync($"/opt/aspire-scripts/get-aspire-cli-pr.sh {GetPullRequestInstallArgs(prNumber)}", counter, TimeSpan.FromSeconds(300));
                await auto.RunCommandAsync("export PATH=~/.aspire/bin:~/.aspire:$PATH", counter);
                break;

            case CliInstallMode.InstallScript:
                await auto.RunCommandFailFastAsync($"/opt/aspire-scripts/get-aspire-cli.sh{GetInstallScriptArgs(strategy)}", counter, TimeSpan.FromSeconds(120));
                await auto.RunCommandAsync("export PATH=~/.aspire/bin:$PATH", counter);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy.Mode, "Unknown install mode");
        }

        await auto.LogInstalledAspireCliVersionAsync(counter);
    }

    /// <summary>
    /// Installs the Aspire CLI in a non-Docker shell using the given install strategy.
    /// </summary>
    internal static async Task InstallAspireCliInShellAsync(
        this Hex1bTerminalAutomator auto,
        CliInstallStrategy strategy,
        SequenceCounter counter)
    {
        switch (strategy.Mode)
        {
            case CliInstallMode.LocalHive:
                var archivePath = QuoteBashArg(strategy.ArchivePath ?? throw new InvalidOperationException("LocalHive strategy is missing the archive path."));
                await auto.ExtractLocalHiveArchiveAsync(archivePath, counter);
                await auto.SourceAspireCliEnvironmentAsync(counter);
                await auto.ConfigureLocalHiveAsync(counter);
                break;

            case CliInstallMode.PullRequest:
                var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
                await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
                await auto.SourceAspireCliEnvironmentAsync(counter);
                break;

            case CliInstallMode.InstallScript:
                var getAspireCliScript = QuoteBashArg(Path.Combine(CliE2ETestHelpers.GetRepoRoot(), "eng", "scripts", "get-aspire-cli.sh"));
                await auto.RunCommandFailFastAsync($"bash {getAspireCliScript}{GetInstallScriptArgs(strategy)}", counter, TimeSpan.FromSeconds(120));
                await auto.SourceAspireCliEnvironmentAsync(counter);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy.Mode, "Unknown install mode");
        }

        await auto.LogInstalledAspireCliVersionAsync(counter);
    }

    /// <summary>
    /// Completes an <c>aspire add</c> command whether it finishes directly or shows a version selection prompt first.
    /// </summary>
    internal static async Task WaitForAspireAddSuccessAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(180);
        var sawVersionPrompt = false;

        await auto.WaitUntilAsync(snapshot =>
        {
            var versionPromptSearcher = new CellPatternSearcher().Find("(based on NuGet.config)");
            if (versionPromptSearcher.Search(snapshot).Count > 0)
            {
                sawVersionPrompt = true;
                return true;
            }

            var successSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");
            var errorSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" ERR:");

            return successSearcher.Search(snapshot).Count > 0 || errorSearcher.Search(snapshot).Count > 0;
        }, timeout: effectiveTimeout, description: $"aspire add completion or version prompt [{counter.Value}]");

        if (sawVersionPrompt)
        {
            await auto.EnterAsync();
        }

        await auto.WaitForSuccessPromptAsync(counter, effectiveTimeout);
    }

    /// <summary>
    /// Mounts the workspace-local Aspire package hive into the standard local hive path inside Docker.
    /// Used for SourceBuild E2E runs where the CLI binary is local but package resolution still needs
    /// locally packed Aspire packages.
    /// </summary>
    internal static async Task MountLocalChannelPackagesAsync(
        this Hex1bTerminalAutomator auto,
        CliE2ETestHelpers.LocalChannelInfo? localChannel,
        TemporaryWorkspace workspace,
        SequenceCounter counter)
    {
        if (localChannel is null)
        {
            return;
        }

        var containerLocalChannelPackagesPath = CliE2ETestHelpers.ToContainerPath(localChannel.PackagesPath, workspace);
        await auto.TypeAsync($"mkdir -p ~/.aspire/hives/local && rm -rf ~/.aspire/hives/local/packages && ln -s '{containerLocalChannelPackagesPath}' ~/.aspire/hives/local/packages");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Prepares a non-Docker terminal environment with prompt counting and workspace navigation.
    /// Used by tests that run with <see cref="CliE2ETestHelpers.CreateTestTerminal"/> (bare bash, no Docker).
    /// </summary>
    internal static async Task PrepareEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        TemporaryWorkspace workspace,
        SequenceCounter counter)
    {
        var waitingForInputPattern = new CellPatternSearcher()
            .Find("b").RightUntil("$").Right(' ').Right(' ');

        await auto.WaitUntilAsync(
            s => waitingForInputPattern.Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "initial bash prompt");
        await auto.WaitAsync(500);

        // Use the same numbered prompt contract as the Docker helpers so the same wait helpers work in both modes.
        const string promptSetup = "CMDCOUNT=0; PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1=\"[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \\$ \"'";
        await auto.TypeAsync(promptSetup);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"cd {workspace.WorkspaceRoot.FullName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"export ASPIRE_E2E_WORKSPACE=\"{workspace.WorkspaceRoot.FullName}\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        if (!CliE2ETestHelpers.IsRunningInCI && ShouldPreserveLocalWorkspace())
        {
            workspace.Preserve();
        }

        if (ShouldCaptureWorkspaceDiagnostics())
        {
            await auto.TypeAsync(
                "trap 'if [ -n \"$ASPIRE_E2E_WORKSPACE\" ]; then " +
                BuildAspireDiagnosticsCaptureCommand("$ASPIRE_E2E_WORKSPACE") +
                "fi' EXIT");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }
    }

    /// <summary>
    /// Installs the Aspire CLI from PR build artifacts in a non-Docker environment.
    /// </summary>
    internal static async Task InstallAspireCliFromPullRequestAsync(
        this Hex1bTerminalAutomator auto,
        int prNumber,
        SequenceCounter counter)
    {
        var command = $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {GetPullRequestInstallArgs(prNumber)}";
        await auto.RunCommandFailFastAsync(command, counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI in a non-Docker environment.
    /// </summary>
    internal static async Task SourceAspireCliEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync("export PATH=~/.aspire/bin:$PATH ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false", counter);
    }

    /// <summary>
    /// Verifies the installed Aspire CLI version matches the expected build.
    /// Always checks the dynamic version prefix from eng/Versions.props.
    /// For non-stabilized builds (all normal PR builds), also verifies the commit SHA suffix.
    /// </summary>
    internal static async Task VerifyAspireCliVersionAsync(
        this Hex1bTerminalAutomator auto,
        string commitSha,
        SequenceCounter counter)
    {
        if (commitSha.Length != 40)
        {
            throw new ArgumentException($"Commit SHA must be exactly 40 characters, got {commitSha.Length}: '{commitSha}'", nameof(commitSha));
        }

        var shortCommitSha = commitSha[..8];

        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();

        // Stabilized PR builds can omit the commit SHA from the printed version, so accept
        // either the expected major/minor marker from eng/Versions.props or the PR commit SHA.
        await auto.WaitUntilAsync(
            snapshot => snapshot.ContainsText(s_expectedStableVersionMarker) || snapshot.ContainsText($"g{shortCommitSha}"),
            timeout: TimeSpan.FromSeconds(10),
            description: $"Aspire CLI version containing '{s_expectedStableVersionMarker}' or 'g{shortCommitSha}'");

        await auto.WaitForSuccessPromptAsync(counter);
    }

    private static string GetExpectedStableVersionMarker()
    {
        var versionsPropsPath = Path.Combine(CliE2ETestHelpers.GetRepoRoot(), "eng", "Versions.props");
        var document = XDocument.Load(versionsPropsPath);

        var majorVersion = document.Descendants("MajorVersion").FirstOrDefault()?.Value;
        var minorVersion = document.Descendants("MinorVersion").FirstOrDefault()?.Value;

        return !string.IsNullOrEmpty(majorVersion) && !string.IsNullOrEmpty(minorVersion)
            ? $"{majorVersion}.{minorVersion}."
            : throw new InvalidOperationException($"Could not determine Aspire version marker from '{versionsPropsPath}'.");
    }

    private static string GetInstallScriptArgs(CliInstallStrategy strategy)
    {
        if (strategy.Quality is not null)
        {
            return $" --quality {strategy.Quality.Value.ToString().ToLowerInvariant()}";
        }

        return strategy.Version is not null
            ? $" --version {strategy.Version}"
            : "";
    }

    private static string QuoteBashArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    /// <summary>
    /// Installs the Aspire CLI and bundle from PR build artifacts, using the PR head SHA to fetch the install script.
    /// </summary>
    internal static async Task InstallAspireBundleFromPullRequestAsync(
        this Hex1bTerminalAutomator auto,
        int prNumber,
        SequenceCounter counter)
    {
        var command = $"ref=$(gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha') && curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {GetPullRequestInstallArgs(prNumber)}";
        await auto.RunCommandFailFastAsync(command, counter, TimeSpan.FromSeconds(300));
    }

    internal static string GetPullRequestInstallArgs(int prNumber)
    {
        var workflowRunId = CliE2ETestHelpers.GetCliArchiveWorkflowRunId();

        return workflowRunId is null
            ? prNumber.ToString(CultureInfo.InvariantCulture)
            : $"{prNumber.ToString(CultureInfo.InvariantCulture)} --run-id {workflowRunId}";
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI bundle in a non-Docker environment.
    /// Unlike <see cref="SourceAspireCliEnvironmentAsync"/>, this includes <c>~/.aspire</c> in PATH for bundle tools.
    /// </summary>
    internal static async Task SourceAspireBundleEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync("export PATH=~/.aspire/bin:~/.aspire:$PATH ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false", counter);
    }

    /// <summary>
    /// Clears the terminal screen by running the <c>clear</c> command and waiting for the prompt.
    /// </summary>
    internal static async Task ClearScreenAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync("clear", counter);
    }

    /// <summary>
    /// Ensures polyglot support is enabled for tests.
    /// Polyglot support now defaults to enabled, so this is currently a no-op.
    /// </summary>
    internal static Task EnablePolyglotSupportAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        _ = auto;
        _ = counter;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enables experimental Java polyglot support for CLI tests.
    /// </summary>
    internal static async Task EnableExperimentalJavaSupportAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("aspire config set features:experimentalPolyglot:java true --global --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs a specific GA version of the Aspire CLI using the install script.
    /// </summary>
    internal static async Task InstallAspireCliVersionAsync(
        this Hex1bTerminalAutomator auto,
        string version,
        SequenceCounter counter)
    {
        var command = $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli.sh | bash -s -- --version \"{version}\"";
        await auto.RunCommandFailFastAsync(command, counter, TimeSpan.FromSeconds(300));
    }

    private static async Task ExtractLocalHiveArchiveAsync(
        this Hex1bTerminalAutomator auto,
        string archivePath,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync($"mkdir -p ~/.aspire && tar -xzf {archivePath} -C ~/.aspire 2>/dev/null", counter, TimeSpan.FromSeconds(30));
    }

    private static async Task ConfigureLocalHiveAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync("aspire config set channel local -g", counter);
        await auto.RunCommandAsync("SDK_VER=$(ls ~/.aspire/hives/local/packages/Aspire.Hosting.*.nupkg 2>/dev/null | head -1 | sed 's/.*Aspire\\.Hosting\\.//;s/\\.nupkg//') && aspire config set sdk.version \"$SDK_VER\" -g", counter);
    }

    private static async Task LogInstalledAspireCliVersionAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync("aspire --version", counter);
    }

    private static async Task RunCommandAsync(
        this Hex1bTerminalAutomator auto,
        string command,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout);
    }

    private static async Task RunCommandFailFastAsync(
        this Hex1bTerminalAutomator auto,
        string command,
        SequenceCounter counter,
        TimeSpan timeout)
    {
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, timeout);
    }

    /// <summary>
    /// Starts an Aspire AppHost with <c>aspire start --format json</c>, extracts the dashboard URL,
    /// and verifies the dashboard is reachable. Caller is responsible for calling
    /// <see cref="AspireStopAsync"/> when done.
    /// On failure, dumps the latest CLI log file to the terminal output and promotes the highest-signal
    /// diagnostics into the workspace for artifact capture.
    /// </summary>
    internal static async Task AspireStartAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? startTimeout = null)
    {
        var effectiveTimeout = startTimeout ?? TimeSpan.FromMinutes(3);
        var expectedCounter = counter.Value;
        // In CI the JSON transcript lives in /tmp first and is copied into the captured workspace on failure.
        // Local runs write directly into the preserved workspace so the file is already where developers inspect it.
        var jsonFile = CliE2ETestHelpers.IsRunningInCI
            ? AspireStartJsonFile
            : "$ASPIRE_E2E_WORKSPACE/_aspire-start.json";

        // Keep aspire start as a single shell pipeline so tee captures the exact JSON emitted to the terminal while
        // pipefail preserves the real CLI exit code instead of letting tee mask build/startup failures.
        await auto.TypeAsync($"(set -o pipefail; aspire start --format json | tee \"{jsonFile}\")");
        await auto.EnterAsync();

        // Wait for the command to finish — check for success or error exit.
        var succeeded = false;
        await auto.WaitUntilAsync(snapshot =>
        {
            var successSearcher = new CellPatternSearcher()
                .FindPattern(expectedCounter.ToString())
                .RightText(" OK] $ ");
            if (successSearcher.Search(snapshot).Count > 0)
            {
                succeeded = true;
                return true;
            }

            var errorSearcher = new CellPatternSearcher()
                .FindPattern(expectedCounter.ToString())
                .RightText(" ERR:");
            return errorSearcher.Search(snapshot).Count > 0;
        }, timeout: effectiveTimeout, description: $"aspire start to complete [{expectedCounter} OK/ERR]");

        counter.Increment();

        if (!succeeded)
        {
            await auto.TypeAsync(
                "LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); " +
                "echo '=== ASPIRE LOG ==='; " +
                "[ -n \"$LOG\" ] && tail -100 \"$LOG\"; " +
                "echo '=== END LOG ==='; " +
                $"cat \"{jsonFile}\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.CaptureRegisteredWorkspaceDiagnosticsAsync(counter);

            var workspacePath = GetRegisteredWorkspacePath();
            throw new InvalidOperationException(
                workspacePath is null || !ShouldCaptureWorkspaceDiagnostics()
                    ? "aspire start failed. Check terminal output for CLI logs."
                    : $"aspire start failed. Workspace: {workspacePath}. See _aspire-detach.log, _aspire-cli.log, .aspire-logs, and _aspire-start.json in the captured workspace.");
        }

        await auto.TypeAsync(
            $"DASHBOARD_URL=$(sed -n " +
            "'s/.*\"dashboardUrl\"[[:space:]]*:[[:space:]]*\"\\(https\\?:\\/\\/localhost:[0-9]*\\).*/\\1/p' " +
            $"\"{jsonFile}\" | head -1)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var dashboardUrlCounter = counter.Value;
        var dashboardUrlFound = false;

        // If DASHBOARD_URL is empty, the apphost likely crashed — dump logs for diagnostics.
        await auto.TypeAsync(
            "if [ -z \"$DASHBOARD_URL\" ]; then " +
            "echo 'dashboard-url-empty'; " +
            "echo '=== ASPIRE START JSON ==='; cat \"" + jsonFile + "\"; echo '=== END JSON ==='; " +
            "echo '=== ALL LOGS ==='; ls -lt ~/.aspire/logs/ 2>/dev/null; echo '=== END LIST ==='; " +
            "DETACH_LOG=$(ls -t ~/.aspire/logs/cli_*detach*.log 2>/dev/null | head -1); " +
            "echo \"=== DETACH LOG: $DETACH_LOG ===\"; [ -n \"$DETACH_LOG\" ] && tail -200 \"$DETACH_LOG\"; echo '=== END DETACH ==='; " +
            "CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | grep -v 'detach' | head -1); " +
            "if [ -z \"$CLI_LOG\" ]; then CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); fi; " +
            "echo \"=== CLI LOG: $CLI_LOG ===\"; [ -n \"$CLI_LOG\" ] && tail -100 \"$CLI_LOG\"; echo '=== END CLI ==='; " +
            "false; " +
            "else " +
            "echo \"dashboard-url:$DASHBOARD_URL\"; " +
            "fi");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(snapshot =>
        {
            var successSearcher = new CellPatternSearcher()
                .FindPattern(dashboardUrlCounter.ToString())
                .RightText(" OK] $ ");
            if (successSearcher.Search(snapshot).Count > 0)
            {
                dashboardUrlFound = true;
                return true;
            }

            var errorSearcher = new CellPatternSearcher()
                .FindPattern(dashboardUrlCounter.ToString())
                .RightText(" ERR:");
            return errorSearcher.Search(snapshot).Count > 0;
        }, timeout: TimeSpan.FromSeconds(30), description: $"dashboard url validation [{dashboardUrlCounter} OK/ERR]");

        counter.Increment();

        if (!dashboardUrlFound)
        {
            // Missing dashboardUrl is the root startup failure we care about. Stop here so a later curl timeout
            // doesn't replace the useful AppHost / detached-child diagnostics with a secondary HTTP symptom.
            await auto.CaptureRegisteredWorkspaceDiagnosticsAsync(counter);

            var workspacePath = GetRegisteredWorkspacePath();
            throw new InvalidOperationException(
                workspacePath is null || !ShouldCaptureWorkspaceDiagnostics()
                    ? "aspire start did not return a dashboard URL. Check terminal output for detached child and CLI logs."
                    : $"aspire start did not return a dashboard URL. Workspace: {workspacePath}. See _aspire-detach.log, _aspire-cli.log, .aspire-logs, and _aspire-start.json in the captured workspace.");
        }

        // Check whether $DASHBOARD_URL was set using variable expansion so the marker
        // text in the output differs from the typed command text. The typed command
        // shows the literal "${DASHBOARD_URL}" on screen, while the shell output
        // shows the expanded value — "URLCHECK::URLEND" when empty.
        await auto.TypeAsync("echo \"URLCHECK:${DASHBOARD_URL}:URLEND\"");
        await auto.EnterAsync();

        var dashboardUrlEmpty = false;
        await auto.WaitUntilAsync(snapshot =>
        {
            var emptySearcher = new CellPatternSearcher().FindPattern("URLCHECK::URLEND");
            if (emptySearcher.Search(snapshot).Count > 0)
            {
                dashboardUrlEmpty = true;
            }

            var promptSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");
            return promptSearcher.Search(snapshot).Count > 0;
        }, timeout: TimeSpan.FromSeconds(30), description: $"dashboard URL check [{counter.Value} OK]");
        counter.Increment();

        if (dashboardUrlEmpty)
        {
            throw new InvalidOperationException(
                "Dashboard URL was empty after aspire start. " +
                "The sed extraction failed to find a dashboardUrl in the JSON output. " +
                "Check terminal output for CLI logs and JSON content.");
        }

        await auto.TypeAsync(
            "curl -ksSL -o /dev/null -w 'dashboard-http-%{http_code}' \"$DASHBOARD_URL\" " +
            "|| echo 'dashboard-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("dashboard-http-200", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Stops a running Aspire AppHost with <c>aspire stop</c>.
    /// </summary>
    internal static async Task AspireStopAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Asserts that the specified resources exist in the running AppHost by running
    /// <c>aspire describe &lt;resource&gt; --format json</c> for each expected resource.
    /// The CLI handles name/displayName resolution internally.
    /// On failure, the error output from the CLI is visible in the terminal recording.
    /// </summary>
    internal static async Task AssertResourcesExistAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        params string[] expectedResourceNames)
    {
        foreach (var resource in expectedResourceNames)
        {
            var expectedCounter = counter.Value;
            await auto.TypeAsync($"aspire describe {resource} --format json");
            await auto.EnterAsync();

            var succeeded = false;
            await auto.WaitUntilAsync(s =>
            {
                var successSearcher = new CellPatternSearcher()
                    .FindPattern(expectedCounter.ToString())
                    .RightText(" OK] $ ");
                if (successSearcher.Search(s).Count > 0)
                {
                    succeeded = true;
                    return true;
                }

                var errorSearcher = new CellPatternSearcher()
                    .FindPattern(expectedCounter.ToString())
                    .RightText(" ERR:");
                return errorSearcher.Search(s).Count > 0;
            }, timeout: TimeSpan.FromSeconds(30), description: $"aspire describe {resource}");

            counter.Increment();

            if (!succeeded)
            {
                // Dump all resources so we can see what's actually running
                await auto.TypeAsync("aspire describe --format json");
                await auto.EnterAsync();
                await auto.WaitForAnyPromptAsync(counter);

                throw new InvalidOperationException(
                    $"Resource '{resource}' not found. 'aspire describe {resource}' exited with an error. " +
                    "Check the terminal recording for the full resource list above.");
            }
        }
    }

    /// <summary>
    /// Copies interesting diagnostics from <c>~/.aspire</c> to the workspace so they are captured by
    /// <see cref="CaptureWorkspaceOnFailureAttribute"/>. Call this before exiting the terminal.
    /// </summary>
    internal static async Task CaptureAspireDiagnosticsAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace)
    {
        if (!CliE2ETestHelpers.IsRunningInCI)
        {
            await auto.TypeAsync("echo diagnostics-available-in-workspace");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
            return;
        }

        var containerWorkspace = $"/workspace/{workspace.WorkspaceRoot.Name}";

        await auto.TypeAsync(BuildAspireDiagnosticsCaptureCommand(containerWorkspace) + "echo done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Destroys the current deployment using <c>aspire destroy --yes</c> and waits for pipeline success.
    /// </summary>
    internal static async Task AspireDestroyAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(2);
        await auto.TypeAsync("aspire destroy --yes");
        await auto.EnterAsync();
        await auto.WaitForPipelineSuccessAsync(timeout: timeout.Value);
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));
    }

    private static async Task CaptureRegisteredWorkspaceDiagnosticsAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        if (!ShouldCaptureWorkspaceDiagnostics())
        {
            return;
        }

        await auto.TypeAsync(
            "if [ -n \"$ASPIRE_E2E_WORKSPACE\" ]; then " +
            BuildAspireDiagnosticsCaptureCommand("$ASPIRE_E2E_WORKSPACE") +
            "echo \"copied-failure-artifacts:$ASPIRE_E2E_WORKSPACE\"; " +
            "fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    private static string BuildAspireDiagnosticsCaptureCommand(string destinationExpression)
    {
        // This returns a single bash fragment because it is reused from EXIT traps and failure paths where the helper
        // needs to inject one inline shell command rather than orchestrate several terminal round-trips.
        return
            $"mkdir -p \"{destinationExpression}\"; " +
            $"rm -rf \"{destinationExpression}/.aspire-logs\" \"{destinationExpression}/.aspire-packages\" \"{destinationExpression}/.aspire-dcp-logs\"; " +
            $"cp -r ~/.aspire/logs \"{destinationExpression}/.aspire-logs\" 2>/dev/null || true; " +
            $"cp -r ~/.aspire/packages \"{destinationExpression}/.aspire-packages\" 2>/dev/null || true; " +
            $"cp -r ~/.aspire/dcp-logs \"{destinationExpression}/.aspire-dcp-logs\" 2>/dev/null || true; " +
            $"cp {AspireStartJsonFile} \"{destinationExpression}/_aspire-start.json\" 2>/dev/null || true; " +
            "DETACH_LOG=$(ls -t ~/.aspire/logs/cli_*detach*.log 2>/dev/null | head -1); " +
            $"[ -n \"$DETACH_LOG\" ] && cp \"$DETACH_LOG\" \"{destinationExpression}/_aspire-detach.log\" 2>/dev/null || true; " +
             "CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | grep -v 'detach' | head -1); " +
             "if [ -z \"$CLI_LOG\" ]; then CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); fi; " +
             $"[ -n \"$CLI_LOG\" ] && cp \"$CLI_LOG\" \"{destinationExpression}/_aspire-cli.log\" 2>/dev/null || true; ";
    }

    private static string? GetRegisteredWorkspacePath()
    {
        if (TestContext.Current?.KeyValueStorage.TryGetValue("WorkspacePath", out var value) == true &&
            value is string workspacePath)
        {
            return workspacePath;
        }

        return null;
    }

    private static bool ShouldPreserveLocalWorkspace()
    {
        return TestContext.Current?.KeyValueStorage.TryGetValue("PreserveWorkspaceOnFailure", out var value) == true &&
            value is true;
    }

    private static bool ShouldCaptureWorkspaceDiagnostics()
    {
        return CliE2ETestHelpers.IsRunningInCI || ShouldPreserveLocalWorkspace();
    }
}
