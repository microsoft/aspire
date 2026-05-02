// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Hex1b.Input;

namespace Aspire.Tests.Shared;

/// <summary>
/// Extension methods for <see cref="Hex1bTerminalAutomator"/> providing Aspire-specific
/// shell prompt detection and common CLI interaction patterns.
/// </summary>
internal static class Hex1bAutomatorTestHelpers
{
    /// <summary>
    /// Waits for a shell success prompt matching the current sequence counter value,
    /// then increments the counter. Looks for the pattern: [N OK] $
    /// </summary>
    internal static async Task WaitForSuccessPromptAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        await auto.WaitUntilAsync(snapshot =>
        {
            var successPromptSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");

            return successPromptSearcher.Search(snapshot).Count > 0;
        }, timeout: effectiveTimeout, description: $"success prompt [{counter.Value} OK] $");

        counter.Increment();
    }

    /// <summary>
    /// Waits for any prompt (success or error) matching the current sequence counter.
    /// </summary>
    internal static async Task WaitForAnyPromptAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        await auto.WaitUntilAsync(snapshot =>
        {
            var successSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");
            var errorSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" ERR:");

            return successSearcher.Search(snapshot).Count > 0 || errorSearcher.Search(snapshot).Count > 0;
        }, timeout: effectiveTimeout, description: $"any prompt [{counter.Value} OK/ERR] $");

        counter.Increment();
    }

    /// <summary>
    /// Repeatedly types a shell command until the first line of command output matches the expected text
    /// or the timeout expires. Each attempt waits for either the first output line or the next prompt,
    /// then waits for the prompt if output appeared first.
    /// </summary>
    internal static async Task ExecuteCommandUntilOutputAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string commandText,
        string desiredOutput,
        TimeSpan? timeout = null,
        TimeSpan? retryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(auto);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredOutput);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var effectiveRetryInterval = retryInterval ?? TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (stopwatch.Elapsed < effectiveTimeout)
        {
            attempt++;
            var expectedPromptSequence = counter.Value;
            var sawPrompt = false;
            var firstOutputMatched = false;

            await auto.TypeAsync(commandText);
            await auto.EnterAsync();

            var remaining = effectiveTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitThisAttempt = remaining < effectiveRetryInterval ? remaining : effectiveRetryInterval;

            try
            {
                await auto.WaitUntilAsync(snapshot =>
                {
                    var firstOutputLine = TryGetFirstOutputLine(snapshot, commandText);
                    if (firstOutputLine is not null)
                    {
                        if (IsPromptLine(firstOutputLine, expectedPromptSequence))
                        {
                            sawPrompt = true;
                            return true;
                        }

                        firstOutputMatched = firstOutputLine.Contains(desiredOutput, StringComparison.Ordinal);
                        sawPrompt = IsPromptVisible(snapshot, expectedPromptSequence);
                        return true;
                    }

                    if (IsPromptVisible(snapshot, expectedPromptSequence))
                    {
                        sawPrompt = true;
                        return true;
                    }

                    return false;
                }, timeout: waitThisAttempt, description: $"waiting for first output or prompt after '{commandText}' (attempt {attempt})");
            }
            catch (TimeoutException) when (stopwatch.Elapsed < effectiveTimeout)
            {
                continue;
            }

            if (!sawPrompt)
            {
                var promptGrace = TimeSpan.FromSeconds(1);
                try
                {
                    await auto.WaitForAnyPromptAsync(counter, promptGrace);
                    sawPrompt = true;
                }
                catch (TimeoutException) when (stopwatch.Elapsed < effectiveTimeout + promptGrace)
                {
                    continue;
                }
            }
            else
            {
                counter.Increment();
            }

            if (firstOutputMatched)
            {
                return; // success
            }

            remaining = effectiveTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delayBeforeRetry = remaining < effectiveRetryInterval ? remaining : effectiveRetryInterval;
            if (delayBeforeRetry > TimeSpan.FromMilliseconds(1))
            {
                await auto.WaitAsync(delayBeforeRetry);
            }
        }

        throw new TimeoutException(
            $"Timed out after {effectiveTimeout} waiting for the first output line from '{commandText}' to contain '{desiredOutput}'.");
    }

    private static bool IsPromptVisible(IHex1bTerminalRegion snapshot, int expectedPromptSequence)
    {
        var successSearcher = new CellPatternSearcher()
            .FindPattern(expectedPromptSequence.ToString())
            .RightText(" OK] $ ");
        var errorSearcher = new CellPatternSearcher()
            .FindPattern(expectedPromptSequence.ToString())
            .RightText(" ERR:");

        return successSearcher.Search(snapshot).Count > 0 || errorSearcher.Search(snapshot).Count > 0;
    }

    private static bool IsPromptLine(string line, int expectedPromptSequence)
    {
        return line.Contains($"[{expectedPromptSequence} OK] $", StringComparison.Ordinal) ||
            line.Contains($"[{expectedPromptSequence} ERR:", StringComparison.Ordinal);
    }

    private static string? TryGetFirstOutputLine(IHex1bTerminalRegion snapshot, string commandText)
    {
        var commandLineIndex = FindCommandLineIndex(snapshot, commandText);
        if (commandLineIndex < 0)
        {
            return null;
        }

        for (var lineIndex = commandLineIndex + 1; lineIndex < snapshot.Height; lineIndex++)
        {
            var line = snapshot.GetLineTrimmed(lineIndex);
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static int FindCommandLineIndex(IHex1bTerminalRegion snapshot, string commandText)
    {
        for (var lineIndex = snapshot.Height - 1; lineIndex >= 0; lineIndex--)
        {
            var line = snapshot.GetLineTrimmed(lineIndex);
            if (line.EndsWith(commandText, StringComparison.Ordinal) ||
                line.Contains($"$ {commandText}", StringComparison.Ordinal) ||
                line.Contains($"# {commandText}", StringComparison.Ordinal))
            {
                return lineIndex;
            }
        }

        for (var lineIndex = snapshot.Height - 1; lineIndex >= 0; lineIndex--)
        {
            var line = snapshot.GetLineTrimmed(lineIndex);
            if (line.Contains(commandText, StringComparison.Ordinal))
            {
                return lineIndex;
            }
        }

        return -1;
    }

    /// <summary>
    /// Waits for a successful command prompt, but fails fast if an error prompt is detected.
    /// </summary>
    internal static async Task WaitForSuccessPromptFailFastAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);
        var sawError = false;

        await auto.WaitUntilAsync(snapshot =>
        {
            var successSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");

            if (successSearcher.Search(snapshot).Count > 0)
            {
                return true;
            }

            var errorSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" ERR:");

            if (errorSearcher.Search(snapshot).Count > 0)
            {
                sawError = true;
                return true;
            }

            return false;
        }, timeout: effectiveTimeout, description: $"success prompt [{counter.Value} OK] $ (fail-fast on error)");

        if (sawError)
        {
            throw new InvalidOperationException(
                $"Command failed with non-zero exit code (detected ERR prompt at sequence {counter.Value}). Check the terminal recording for details.");
        }

        counter.Increment();
    }

    /// <summary>
    /// Types a shell command, waits for it to complete successfully, and advances the prompt counter.
    /// </summary>
    internal static async Task RunCommandAsync(
        this Hex1bTerminalAutomator auto,
        string command,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout);
    }

    /// <summary>
    /// Types a shell command, waits for it to complete successfully, and fails immediately on a shell error prompt.
    /// </summary>
    internal static async Task RunCommandFailFastAsync(
        this Hex1bTerminalAutomator auto,
        string command,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        await auto.TypeAsync(command);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, timeout);
    }

    /// <summary>
    /// Configures a numbered bash prompt and changes into the provided workspace directory.
    /// </summary>
    internal static async Task PrepareBashEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        string workspacePath,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var waitingForInputPattern = new CellPatternSearcher()
            .Find("b")
            .RightUntil("$")
            .Right(' ')
            .Right(' ');

        await auto.WaitUntilAsync(
            s => waitingForInputPattern.Search(s).Count > 0,
            timeout: effectiveTimeout,
            description: "initial bash prompt");
        await auto.WaitAsync(500);

        await auto.TypeAsync(AspireCliShellCommandHelpers.NumberedPromptSetupCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(workspacePath)}", counter);
    }

    /// <summary>
    /// Extracts a localhive archive into <c>~/.aspire</c>.
    /// </summary>
    internal static async Task ExtractLocalHiveArchiveAsync(
        this Hex1bTerminalAutomator auto,
        string archivePath,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync(
            AspireCliShellCommandHelpers.GetExtractLocalHiveArchiveCommand(archivePath),
            counter,
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Configures Aspire to use the extracted localhive packages.
    /// </summary>
    internal static async Task ConfigureLocalHiveAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        foreach (var command in AspireCliShellCommandHelpers.GetConfigureLocalHiveCommands())
        {
            await auto.RunCommandAsync(command, counter);
        }
    }

    /// <summary>
    /// Sources the standard <c>~/.aspire</c> environment for CLI or bundle execution.
    /// </summary>
    internal static async Task SourceAspireEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        bool includeBundlePath = false)
    {
        await auto.RunCommandAsync(
            AspireCliShellCommandHelpers.GetSourceAspireEnvironmentCommand(includeBundlePath),
            counter,
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Logs the installed Aspire CLI version.
    /// </summary>
    internal static async Task LogAspireCliVersionAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync(
            AspireCliShellCommandHelpers.AspireCliVersionCommand,
            counter,
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Waits for <c>aspire add</c> to either finish directly or stop on the version-selection prompt.
    /// </summary>
    internal static async Task WaitForAspireAddCompletionAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var sawVersionPrompt = false;
        var successPrompt = new CellPatternSearcher()
            .FindPattern(counter.Value.ToString())
            .RightText(" OK] $ ");
        var errorPrompt = new CellPatternSearcher()
            .FindPattern(counter.Value.ToString())
            .RightText(" ERR:");
        var waitingForVersionSelection = new CellPatternSearcher()
            .Find("What version would you like to install?");
        var waitingForLegacyVersionSelection = new CellPatternSearcher()
            .Find("based on NuGet.config");

        // We intentionally do NOT check for completion text like "was added successfully"
        // because it persists on the terminal from prior aspire-add runs and would match
        // stale output when multiple aspire-add commands run in sequence. Instead we rely
        // on the shell success/error prompt which uses a unique counter value.

        await auto.WaitUntilAsync(s =>
            {
                if (waitingForVersionSelection.Search(s).Count > 0 || waitingForLegacyVersionSelection.Search(s).Count > 0)
                {
                    sawVersionPrompt = true;
                    return true;
                }

                return successPrompt.Search(s).Count > 0
                    || errorPrompt.Search(s).Count > 0;
            },
            timeout: effectiveTimeout,
            description: "aspire add completion or version-selection prompt");

        if (!sawVersionPrompt)
        {
            await auto.WaitForSuccessPromptFailFastAsync(counter, effectiveTimeout);
            return;
        }

        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, effectiveTimeout);
    }

    /// <summary>
    /// Handles the agent init confirmation prompt that appears after aspire init/new,
    /// then waits for the shell success prompt. Supports CLI versions with and without agent init chaining.
    /// </summary>
    internal static async Task DeclineAgentInitPromptAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        var agentInitPrompt = new CellPatternSearcher()
            .Find("configure AI agent environments");

        var agentInitFound = false;
        var errorPromptFound = false;

        // Wait for either the agent init prompt (new CLI) or the success prompt (old CLI).
        await auto.WaitUntilAsync(s =>
        {
            if (agentInitPrompt.Search(s).Count > 0)
            {
                agentInitFound = true;
                return true;
            }
            var successSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");
            if (successSearcher.Search(s).Count > 0)
            {
                return true;
            }

            var errorSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" ERR:");
            errorPromptFound = errorSearcher.Search(s).Count > 0;
            return errorPromptFound;
        }, timeout: effectiveTimeout, description: $"agent init prompt, success prompt [{counter.Value} OK] $, or error prompt [{counter.Value} ERR:*] $");

        if (errorPromptFound)
        {
            throw new InvalidOperationException($"Command failed with error prompt [{counter.Value} ERR:*] while waiting for the agent init prompt or success prompt.");
        }

        if (!agentInitFound)
        {
            counter.Increment();
            return;
        }

        await auto.WaitAsync(500);
        await auto.TypeAsync("n");

        // Some prompts accept a single "n" immediately, while others require Enter.
        // Only send Enter if the command did not already finish to avoid a phantom
        // blank shell command that advances CMDCOUNT and desyncs prompt tracking.
        var successPromptFound = false;
        try
        {
            await auto.WaitUntilAsync(s =>
            {
                var successSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");
                successPromptFound = successSearcher.Search(s).Count > 0;

                return successPromptFound;
            }, timeout: TimeSpan.FromSeconds(2), description: $"success prompt [{counter.Value} OK] $ after agent init response");
        }
        catch (Hex1bAutomationException)
        {
        }

        if (successPromptFound)
        {
            counter.Increment();
            return;
        }

        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, effectiveTimeout);
    }

    /// <summary>
    /// Runs <c>aspire new</c> interactively, selecting the specified template and responding to all prompts.
    /// </summary>
    internal static async Task AspireNewAsync(
        this Hex1bTerminalAutomator auto,
        string projectName,
        SequenceCounter counter,
        AspireTemplate template = AspireTemplate.Starter,
        bool useRedisCache = true)
    {
        var templateTimeout = TimeSpan.FromSeconds(60);

        // Step 1: Type aspire new and wait for the template list
        await auto.TypeAsync("aspire new");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> Starter App").Search(s).Count > 0,
            timeout: templateTimeout,
            description: "template selection list (> Starter App)");

        // Step 2: Navigate to and select the desired template
        switch (template)
        {
            case AspireTemplate.Starter:
                await auto.EnterAsync(); // First option, no navigation needed
                break;

            case AspireTemplate.JsReact:
                await auto.DownAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Starter App (ASP.NET Core/React, C# AppHost)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "JS React template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.ExpressReact:
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Starter App (Express/React, TypeScript AppHost)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "Express React template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.PythonReact:
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Starter App (FastAPI/React, TypeScript AppHost)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "Python React template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.EmptyAppHost:
                await auto.TypeAsync("Empty AppHost");
                await auto.EnterAsync();
                try
                {
                    await auto.WaitUntilAsync(
                        s => new CellPatternSearcher().Find("Which language would you like to use?").Search(s).Count > 0,
                        timeout: TimeSpan.FromSeconds(5),
                        description: "AppHost language prompt");
                    await auto.EnterAsync();
                }
                catch (Hex1bAutomationException)
                {
                }
                break;

            case AspireTemplate.TypeScriptEmptyAppHost:
                await auto.TypeAsync("Empty AppHost");
                await auto.EnterAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("Which language would you like to use?").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(10),
                    description: "AppHost language prompt");
                await auto.TypeAsync("TypeScript");
                await auto.EnterAsync();
                break;

            case AspireTemplate.JavaEmptyAppHost:
                await auto.TypeAsync("Empty AppHost");
                await auto.EnterAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("Which language would you like to use?").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(10),
                    description: "AppHost language prompt");
                await auto.TypeAsync("Java");
                await auto.EnterAsync();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(template), template, $"Unsupported template: {template}");
        }
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the project name").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "project name prompt");
        await auto.TypeAsync(projectName);
        await auto.EnterAsync();

        // Step 4: Accept default output path
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the output path").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "output path prompt");
        await auto.EnterAsync();

        // Step 5: URLs prompt (all templates have this)
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Use *.dev.localhost URLs").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "URLs prompt");
        await auto.EnterAsync(); // Accept default "No"

        // Step 6: Redis prompt (only Starter, JsReact, PythonReact)
        if (template is AspireTemplate.Starter or AspireTemplate.JsReact or AspireTemplate.PythonReact)
        {
            await auto.WaitUntilAsync(
                s => new CellPatternSearcher().Find("Use Redis Cache").Search(s).Count > 0,
                timeout: TimeSpan.FromSeconds(10),
                description: "Redis cache prompt");

            if (!useRedisCache)
            {
                await auto.TypeAsync("n");
                await auto.EnterAsync();
            }
            else
            {
                await auto.EnterAsync();
            }
        }

        // Step 7: Test project prompt (only Starter)
        if (template is AspireTemplate.Starter)
        {
            await auto.WaitUntilAsync(
                s => new CellPatternSearcher().Find("Do you want to create a test project?").Search(s).Count > 0,
                timeout: TimeSpan.FromSeconds(10),
                description: "test project prompt");
            await auto.EnterAsync(); // Accept default "No"
        }

        // Step 8: Decline the agent init prompt and wait for success
        await auto.DeclineAgentInitPromptAsync(counter);
    }

    /// <summary>
    /// Installs dependencies, runs <c>aspire restore</c>, and then type-checks the generated TypeScript project.
    /// </summary>
    internal static async Task AspireRestoreAndTypeCheckTypeScriptAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string typeCheckCommand = "npx --no-install tsc --noEmit --project tsconfig.apphost.json",
        TimeSpan? installTimeout = null,
        TimeSpan? restoreTimeout = null,
        TimeSpan? typeCheckTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeCheckCommand);

        await auto.TypeAsync("npm install");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, installTimeout ?? TimeSpan.FromMinutes(5));

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(
            "SDK code restored successfully",
            timeout: restoreTimeout ?? TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync(typeCheckCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, typeCheckTimeout ?? TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Runs <c>aspire init --language csharp</c> and handles the NuGet.config, URLs, and agent init prompts.
    /// </summary>
    internal static async Task AspireInitAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        var waitingForNuGetConfigPrompt = new CellPatternSearcher()
            .Find("NuGet.config");

        var waitingForUrlsPrompt = new CellPatternSearcher()
            .Find("Use *.dev.localhost URLs");

        var waitingForInitComplete = new CellPatternSearcher()
            .Find("Aspire initialization complete");

        var waitingForAgentInitPrompt = new CellPatternSearcher()
            .Find("configure AI agent environments");

        await auto.TypeAsync("aspire init --language csharp");
        await auto.EnterAsync();

        var handledNuGetConfigPrompt = false;
        var handledUrlsPrompt = false;

        while (true)
        {
            var initState = "unknown";
            await auto.WaitUntilAsync(s =>
            {
                if (!handledNuGetConfigPrompt && waitingForNuGetConfigPrompt.Search(s).Count > 0)
                {
                    initState = "nuget-config";
                    return true;
                }

                if (!handledUrlsPrompt && waitingForUrlsPrompt.Search(s).Count > 0)
                {
                    initState = "urls";
                    return true;
                }

                if (waitingForAgentInitPrompt.Search(s).Count > 0)
                {
                    initState = "agent-init";
                    return true;
                }

                if (waitingForInitComplete.Search(s).Count > 0)
                {
                    initState = "init-complete";
                    return true;
                }

                return false;
            }, timeout: TimeSpan.FromMinutes(2), description: "NuGet.config prompt, URLs prompt, agent init prompt, or init completion");

            if (initState is "nuget-config" or "urls")
            {
                if (initState == "nuget-config")
                {
                    handledNuGetConfigPrompt = true;
                }
                else
                {
                    handledUrlsPrompt = true;
                }

                await auto.EnterAsync();
                continue;
            }

            await auto.DeclineAgentInitPromptAsync(counter);
            return;
        }
    }

    /// <summary>
    /// Waits for the deploy/destroy pipeline to complete, failing immediately if the pipeline reports failure
    /// instead of waiting for the full timeout to elapse.
    /// </summary>
    internal static async Task WaitForPipelineSuccessAsync(
        this Hex1bTerminalAutomator auto,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var pipelineSucceeded = false;
        string? terminalOutput = null;

        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText(ConsoleActivityLoggerStrings.PipelineFailed))
            {
                terminalOutput = s.GetText();
                return true;
            }

            if (s.ContainsText(ConsoleActivityLoggerStrings.PipelineSucceeded))
            {
                pipelineSucceeded = true;
                return true;
            }

            return false;
        }, timeout: effectiveTimeout, description: "pipeline succeeded or failed");

        if (!pipelineSucceeded)
        {
            throw new InvalidOperationException($"Pipeline failed unexpectedly. Terminal output:{Environment.NewLine}{terminalOutput}");
        }
    }
}
