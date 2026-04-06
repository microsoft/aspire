// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Extension methods for <see cref="Hex1bTerminalAutomator"/> providing Docker E2E test helpers.
/// These parallel the <see cref="Hex1b.Automation.Hex1bTerminalInputSequenceBuilder"/>-based methods in <see cref="CliE2ETestHelpers"/>.
/// </summary>
internal static class CliE2EAutomatorHelpers
{
    /// <summary>
    /// Prepares the Docker environment by setting up prompt counting, umask, and environment variables.
    /// </summary>
    internal static async Task PrepareDockerEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace? workspace = null)
    {
        // Wait for container to be ready (root prompt)
        await auto.WaitUntilTextAsync("# ", timeout: TimeSpan.FromSeconds(60));

        await auto.WaitAsync(500);

        // Set up the prompt counting mechanism
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

        if (workspace is not null)
        {
            await auto.TypeAsync($"cd /workspace/{workspace.WorkspaceRoot.Name}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Set up EXIT trap to copy .aspire diagnostics to workspace for CI capture
            await auto.TypeAsync($"trap 'cp -r ~/.aspire/logs /workspace/{workspace.WorkspaceRoot.Name}/.aspire-logs 2>/dev/null; cp -r ~/.aspire/packages /workspace/{workspace.WorkspaceRoot.Name}/.aspire-packages 2>/dev/null' EXIT");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
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
                await auto.TypeAsync($"/opt/aspire-scripts/get-aspire-cli-pr.sh {prNumber}");
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

        const string promptSetup = "CMDCOUNT=0; PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1=\"[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \\$ \"'";
        await auto.TypeAsync(promptSetup);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"cd {workspace.WorkspaceRoot.FullName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Aspire CLI from PR build artifacts in a non-Docker environment.
    /// </summary>
    internal static async Task InstallAspireCliFromPullRequestAsync(
        this Hex1bTerminalAutomator auto,
        int prNumber,
        SequenceCounter counter)
    {
        var command = $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {prNumber}";
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI in a non-Docker environment.
    /// </summary>
    internal static async Task SourceAspireCliEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("export PATH=~/.aspire/bin:$PATH ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
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
        var versionPrefix = CliE2ETestHelpers.GetVersionPrefix();
        var isStabilized = CliE2ETestHelpers.IsStabilizedBuild();

        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();

        // Always verify the version prefix matches the branch's version (e.g., "13.3.0").
        await auto.WaitUntilTextAsync(versionPrefix, timeout: TimeSpan.FromSeconds(10));

        // For non-stabilized builds (all PR CI builds), also verify the commit SHA suffix
        // to uniquely identify the exact build. Stabilized builds (official releases only)
        // produce versions without SHA suffixes, so we skip this check.
        if (!isStabilized && commitSha.Length == 40)
        {
            var shortCommitSha = commitSha[..8];
            var expectedVersionSuffix = $"g{shortCommitSha}";
            await auto.WaitUntilTextAsync(expectedVersionSuffix, timeout: TimeSpan.FromSeconds(10));
        }

        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Aspire CLI and bundle from PR build artifacts, using the PR head SHA to fetch the install script.
    /// </summary>
    internal static async Task InstallAspireBundleFromPullRequestAsync(
        this Hex1bTerminalAutomator auto,
        int prNumber,
        SequenceCounter counter)
    {
        var command = $"ref=$(gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha') && curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {prNumber}";
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI bundle in a non-Docker environment.
    /// Unlike <see cref="SourceAspireCliEnvironmentAsync"/>, this includes <c>~/.aspire</c> in PATH for bundle tools.
    /// </summary>
    internal static async Task SourceAspireBundleEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("export PATH=~/.aspire/bin:~/.aspire:$PATH ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Clears the terminal screen by running the <c>clear</c> command and waiting for the prompt.
    /// </summary>
    internal static async Task ClearScreenAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("clear");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
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
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, timeout: TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Starts an Aspire AppHost with <c>aspire start --format json</c>, extracts the dashboard URL,
    /// and verifies the dashboard is reachable. Caller is responsible for calling
    /// <see cref="AspireStopAsync"/> when done.
    /// On failure, dumps the latest CLI log file to the terminal output for debugging.
    /// </summary>
    internal static async Task AspireStartAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? startTimeout = null)
    {
        var effectiveTimeout = startTimeout ?? TimeSpan.FromMinutes(3);
        var jsonFile = "/tmp/aspire-start.json";
        var expectedCounter = counter.Value;

        // Start with JSON output
        await auto.TypeAsync($"aspire start --format json | tee {jsonFile}");
        await auto.EnterAsync();

        // Wait for the command to finish — check for success or error exit
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
            // Dump logs for debugging then fail
            await auto.TypeAsync(
                "LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); " +
                "echo '=== ASPIRE LOG ==='; " +
                "[ -n \"$LOG\" ] && tail -100 \"$LOG\"; " +
                "echo '=== END LOG ==='; " +
                $"cat {jsonFile}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            throw new InvalidOperationException("aspire start failed. Check terminal output for CLI logs.");
        }

        // Extract dashboard URL and verify it's reachable.
        // First check if the apphost crashed (aspire start exits 0 but prints error).
        await auto.TypeAsync(
            $"DASHBOARD_URL=$(sed -n " +
            "'s/.*\"dashboardUrl\"[[:space:]]*:[[:space:]]*\"\\(https\\?:\\/\\/localhost:[0-9]*\\).*/\\1/p' " +
            $"{jsonFile} | head -1)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // If DASHBOARD_URL is empty, the apphost likely crashed — dump logs for diagnostics
        await auto.TypeAsync(
            "if [ -z \"$DASHBOARD_URL\" ]; then " +
            "echo 'dashboard-url-empty'; " +
            "echo '=== ASPIRE START JSON ==='; cat " + jsonFile + "; echo '=== END JSON ==='; " +
            "echo '=== ALL LOGS ==='; ls -lt ~/.aspire/logs/ 2>/dev/null; echo '=== END LIST ==='; " +
            "DETACH_LOG=$(ls -t ~/.aspire/logs/cli_*detach*.log 2>/dev/null | head -1); " +
            "echo \"=== DETACH LOG: $DETACH_LOG ===\"; [ -n \"$DETACH_LOG\" ] && tail -200 \"$DETACH_LOG\"; echo '=== END DETACH ==='; " +
            "CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); " +
            "echo \"=== CLI LOG: $CLI_LOG ===\"; [ -n \"$CLI_LOG\" ] && tail -100 \"$CLI_LOG\"; echo '=== END CLI ==='; " +
            "fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

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
    /// Copies interesting diagnostic directories from <c>~/.aspire</c> to the mounted workspace
    /// so they are captured by <see cref="CaptureWorkspaceOnFailureAttribute"/>. Call this before
    /// exiting the container. Copies logs and NuGet restore output (libs directories).
    /// </summary>
    internal static async Task CaptureAspireDiagnosticsAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace)
    {
        var containerWorkspace = $"/workspace/{workspace.WorkspaceRoot.Name}";

        // Copy CLI logs
        await auto.TypeAsync($"cp -r ~/.aspire/logs {containerWorkspace}/.aspire-logs 2>/dev/null; " +
                             $"cp -r ~/.aspire/packages {containerWorkspace}/.aspire-packages 2>/dev/null; " +
                             "echo done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
