// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

/// <summary>
/// Extension methods for <see cref="Hex1bTerminalAutomator"/> providing deployment E2E test patterns.
/// These parallel the <see cref="Hex1bTerminalInputSequenceBuilder"/>-based methods in <see cref="DeploymentE2ETestHelpers"/>.
/// </summary>
internal static class DeploymentE2EAutomatorHelpers
{
    /// <summary>
    /// Prepares the terminal environment with a custom prompt for command tracking.
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

        // Bash prompt setup with command tracking
        const string promptSetup = "CMDCOUNT=0; PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1=\"[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \\$ \"'";
        await auto.TypeAsync(promptSetup);
        await auto.EnterAsync();

        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"cd {workspace.WorkspaceRoot.FullName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Aspire CLI from PR build artifacts.
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
    /// Installs the latest GA (release quality) Aspire CLI.
    /// </summary>
    internal static async Task InstallAspireCliReleaseAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        var command = "curl -fsSL https://aka.ms/aspire/get/install.sh | bash -s -- --quality release";

        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI.
    /// </summary>
    internal static async Task SourceAspireCliEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("export PATH=~/.aspire/bin:$PATH ASPIRE_PLAYGROUND=true DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Aspire CLI Bundle from a specific pull request's artifacts.
    /// The bundle includes the native AOT CLI, .NET runtime, Dashboard, DCP, and AppHost Server.
    /// </summary>
    internal static async Task InstallAspireBundleFromPullRequestAsync(
        this Hex1bTerminalAutomator auto,
        int prNumber,
        SequenceCounter counter)
    {
        var command = $"ref=$(gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha') && " +
                      $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {prNumber}";

        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Sources the Aspire Bundle environment after installation.
    /// Adds both the bundle's bin/ and root directories to PATH.
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
    /// Waits for <c>aspire add</c> to complete, handling the optional version selection prompt.
    /// In CI with bundle install, the CLI may or may not show a version selection prompt.
    /// This method waits for either the prompt or the success prompt, and dismisses the prompt if shown.
    /// </summary>
    internal static async Task WaitForAspireAddCompletionAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(180);

        var versionPrompt = new CellPatternSearcher()
            .Find("based on NuGet.config");
        var successPrompt = new CellPatternSearcher()
            .FindPattern(counter.Value.ToString())
            .RightText(" OK] $ ");

        var sawVersionPrompt = false;
        await auto.WaitUntilAsync(s =>
        {
            if (versionPrompt.Search(s).Count > 0)
            {
                sawVersionPrompt = true;
                return true;
            }
            return successPrompt.Search(s).Count > 0;
        }, timeout: effectiveTimeout, description: $"version prompt or success prompt [{counter.Value} OK] $");

        if (sawVersionPrompt)
        {
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, effectiveTimeout);
        }
        else
        {
            counter.Increment();
        }
    }

    /// <summary>
    /// Destroys the current deployment using <c>aspire destroy --yes</c> and waits for pipeline success.
    /// </summary>
    internal static async Task AspireDestroyAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);
        await auto.TypeAsync("aspire destroy --yes");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(Aspire.Cli.Resources.ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: timeout.Value);
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }
}
