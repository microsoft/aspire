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
            return successSearcher.Search(s).Count > 0;
        }, timeout: effectiveTimeout, description: $"agent init prompt or success prompt [{counter.Value} OK] $");

        await auto.WaitAsync(500);

        // Type 'n' + Enter unconditionally:
        // - Agent init: declines the prompt, CLI exits, success prompt appears
        // - No agent init: 'n' runs at bash (command not found), produces error prompt
        await auto.TypeAsync("n");
        await auto.EnterAsync();

        // Wait for the aspire command's success prompt
        await auto.WaitUntilAsync(s =>
        {
            var successSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");
            return successSearcher.Search(s).Count > 0;
        }, timeout: effectiveTimeout, description: $"success prompt [{counter.Value} OK] $ after agent init");

        // Increment counter correctly for both cases
        if (!agentInitFound)
        {
            counter.Increment();
        }
        counter.Increment();
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
                    s => new CellPatternSearcher().Find("> Starter App (ASP.NET Core/React)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "JS React template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.ExpressReact:
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Starter App (Express/React)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "Express React template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.PythonReact:
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.DownAsync();
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Starter App (FastAPI/React)").Search(s).Count > 0,
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
                await auto.TypeAsync("TypeScript");
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Empty (TypeScript AppHost)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "TypeScript Empty AppHost template selected");
                await auto.EnterAsync();
                break;

            case AspireTemplate.JavaEmptyAppHost:
                await auto.TypeAsync("Empty (Java AppHost)");
                await auto.WaitUntilAsync(
                    s => new CellPatternSearcher().Find("> Empty (Java AppHost)").Search(s).Count > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    description: "Java Empty AppHost template selected");
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
                await auto.DownAsync(); // Default is "Yes", navigate to "No"
            }

            await auto.EnterAsync();
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
    /// Runs <c>aspire init --language csharp</c> and handles the NuGet.config and agent init prompts.
    /// </summary>
    internal static async Task AspireInitAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        var waitingForNuGetConfigPrompt = new CellPatternSearcher()
            .Find("NuGet.config");

        var waitingForInitComplete = new CellPatternSearcher()
            .Find("Aspire initialization complete");

        await auto.TypeAsync("aspire init --language csharp");
        await auto.EnterAsync();

        // NuGet.config prompt may or may not appear depending on environment.
        // Wait for either the NuGet.config prompt or init completion.
        await auto.WaitUntilAsync(
            s => waitingForNuGetConfigPrompt.Search(s).Count > 0
                || waitingForInitComplete.Search(s).Count > 0,
            timeout: TimeSpan.FromMinutes(2),
            description: "NuGet.config prompt or init completion");
        await auto.EnterAsync(); // Dismiss NuGet.config prompt if present

        await auto.WaitUntilAsync(
            s => waitingForInitComplete.Search(s).Count > 0,
            timeout: TimeSpan.FromMinutes(2),
            description: "aspire initialization complete");

        await auto.DeclineAgentInitPromptAsync(counter);
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
