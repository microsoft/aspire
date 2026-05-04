// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace Aspire.Tests.Shared;

/// <summary>
/// Tracks the sequence number for shell prompt detection in Hex1b terminal sessions.
/// </summary>
internal sealed class SequenceCounter
{
    public int Value { get; private set; } = 1;

    public int Increment()
    {
        return ++Value;
    }
}

/// <summary>
/// Represents the available Aspire project templates for <c>aspire new</c>.
/// </summary>
internal enum AspireTemplate
{
    /// <summary>
    /// Starter App (ASP.NET Core/Blazor).
    /// Prompts: template, project name, output path, URLs, Redis, test project.
    /// </summary>
    Starter,

    /// <summary>
    /// Starter App (ASP.NET Core/React, C# AppHost).
    /// Prompts: template, project name, output path, URLs, Redis. No test project prompt.
    /// </summary>
    JsReact,

    /// <summary>
    /// Starter App (Express/React, TypeScript AppHost).
    /// Prompts: template, project name, output path, URLs. No Redis or test project prompt.
    /// </summary>
    ExpressReact,

    /// <summary>
    /// Starter App (FastAPI/React, TypeScript AppHost).
    /// Prompts: template, project name, output path, URLs, Redis. No test project prompt.
    /// </summary>
    PythonReact,

    /// <summary>
    /// Empty (TypeScript AppHost).
    /// Prompts: template, project name, output path, URLs. No language, Redis, or test project prompt.
    /// </summary>
    TypeScriptEmptyAppHost,

    /// <summary>
    /// Empty AppHost.
    /// Prompts: template, language, project name, output path, URLs. No Redis or test project prompt.
    /// </summary>
    EmptyAppHost,

    /// <summary>
    /// Empty (Java AppHost) — visible only when experimental Java support is enabled.
    /// Prompts: template, project name, output path, URLs. No Redis or test project prompt.
    /// </summary>
    JavaEmptyAppHost,

    /// <summary>
    /// Empty (Python AppHost) — visible only when experimental Python support is enabled.
    /// Prompts: template, project name, output path, URLs. No Redis or test project prompt.
    /// </summary>
    PythonEmptyAppHost,
}

/// <summary>
/// Shared helper methods for creating and managing Hex1b terminal sessions across E2E test projects.
/// </summary>
internal static class Hex1bTestHelpers
{
    /// <summary>
    /// Creates a headless Hex1b terminal configured for E2E testing with asciinema recording.
    /// Uses default dimensions of 160x48 unless overridden.
    /// </summary>
    /// <param name="testName">The test name used for the recording file path. Defaults to the calling method name.</param>
    /// <param name="localSubDir">The subdirectory name under the local <c>TestResults/recordings</c> directory.</param>
    /// <param name="width">The terminal width in columns. Defaults to 160.</param>
    /// <param name="height">The terminal height in rows. Defaults to 48.</param>
    /// <returns>A configured <see cref="Hex1bTerminal"/> instance. Caller is responsible for disposal.</returns>
    internal static Hex1bTerminal CreateTestTerminal(
        string localSubDir,
        int width = 160,
        int height = 48,
        [CallerMemberName] string testName = "")
    {
        var recordingPath = GetTestResultsRecordingPath(testName, localSubDir);

        var builder = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
            .WithAsciinemaRecording(recordingPath)
            .WithPtyProcess("/bin/bash", ["--norc"]);

        return builder.Build();
    }

    /// <summary>
    /// Gets the path for storing asciinema recordings that will be uploaded as CI artifacts.
    /// In CI, this returns a path under $GITHUB_WORKSPACE/testresults/recordings/.
    /// Locally, this returns a path under the test output <c>TestResults/recordings/</c> directory.
    /// </summary>
    /// <param name="testName">The name of the test (used as the recording filename).</param>
    /// <param name="localSubDir">The subdirectory name under the local <c>TestResults/recordings</c> directory.</param>
    /// <returns>The full path to the .cast recording file.</returns>
    internal static string GetTestResultsRecordingPath(string testName, string localSubDir)
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        string recordingsDir;

        if (!string.IsNullOrEmpty(githubWorkspace))
        {
            // CI environment - write directly to test results for artifact upload
            recordingsDir = Path.Combine(githubWorkspace, "testresults", "recordings");
        }
        else
        {
            // Local development - keep recordings with the rest of the test output.
            recordingsDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "recordings", localSubDir);
        }

        Directory.CreateDirectory(recordingsDir);
        return Path.Combine(recordingsDir, $"{testName}.cast");
    }

    /// <summary>
    /// Waits for a successful command prompt with the expected sequence number.
    /// </summary>
    internal static Hex1bTerminalInputSequenceBuilder WaitForSuccessPrompt(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        return builder.WaitUntil(snapshot =>
            {
                var successPromptSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");

                var result = successPromptSearcher.Search(snapshot);
                return result.Count > 0;
            }, effectiveTimeout)
            .IncrementSequence(counter);
    }

    /// <summary>
    /// Waits for any prompt (success or error) matching the current sequence counter.
    /// Use this when the command is expected to return a non-zero exit code.
    /// </summary>
    internal static Hex1bTerminalInputSequenceBuilder WaitForAnyPrompt(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        return builder.WaitUntil(snapshot =>
            {
                var successSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");
                var errorSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" ERR:");

                return successSearcher.Search(snapshot).Count > 0 || errorSearcher.Search(snapshot).Count > 0;
            }, effectiveTimeout)
            .IncrementSequence(counter);
    }

    /// <summary>
    /// Waits for the shell prompt to show a non-zero exit code pattern: [N ERR:code] $
    /// This is used to verify that a command exited with a failure code.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="counter">The sequence counter for prompt detection.</param>
    /// <param name="exitCode">The expected non-zero exit code.</param>
    /// <param name="timeout">Optional timeout (defaults to 500 seconds).</param>
    /// <returns>The builder for chaining.</returns>
    internal static Hex1bTerminalInputSequenceBuilder WaitForErrorPrompt(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter,
        int exitCode = 1,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        return builder.WaitUntil(snapshot =>
            {
                var errorPromptSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText($" ERR:{exitCode}] $ ");

                var result = errorPromptSearcher.Search(snapshot);
                return result.Count > 0;
            }, effectiveTimeout)
            .IncrementSequence(counter);
    }

    /// <summary>
    /// Waits for a successful command prompt, but fails fast if an error prompt is detected.
    /// Unlike <see cref="WaitForSuccessPrompt"/>, this method also watches for error prompts
    /// (ERR:N pattern) and throws immediately instead of waiting for the full timeout.
    /// Use this for commands that may fail due to transient errors (e.g., CLI downloads).
    /// </summary>
    internal static Hex1bTerminalInputSequenceBuilder WaitForSuccessPromptFailFast(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);
        var sawError = false;

        return builder.WaitUntil(snapshot =>
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
            }, effectiveTimeout)
            .WaitUntil(_ =>
            {
                if (sawError)
                {
                    throw new InvalidOperationException(
                        $"Command failed with non-zero exit code (detected ERR prompt at sequence {counter.Value}). Check the terminal recording for details.");
                }

                counter.Increment();
                return true;
            }, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Increments the sequence counter.
    /// </summary>
    internal static Hex1bTerminalInputSequenceBuilder IncrementSequence(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter)
    {
        return builder.WaitUntil(s =>
        {
            counter.Increment();
            return true;
        }, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Executes an arbitrary callback action during the sequence execution.
    /// This is useful for performing file modifications or other side effects between terminal commands.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="callback">The callback action to execute.</param>
    /// <returns>The builder for chaining.</returns>
    internal static Hex1bTerminalInputSequenceBuilder ExecuteCallback(
        this Hex1bTerminalInputSequenceBuilder builder,
        Action callback)
    {
        return builder.WaitUntil(s =>
        {
            callback();
            return true;
        }, TimeSpan.FromSeconds(1));
    }
    /// <summary>
    /// Declines the agent init confirmation prompt that appears after <c>aspire init</c> or <c>aspire new</c>.
    /// Does NOT wait for the shell success prompt — callers must chain their own
    /// <see cref="WaitForSuccessPrompt"/> when using this overload.
    /// Used by deployment tests that need custom timeouts for WaitForSuccessPrompt.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="timeout">
    /// How long to wait for the prompt. This must cover the time for project creation
    /// (<c>dotnet new</c>) to finish plus the prompt appearing. Defaults to 120 seconds
    /// to accommodate slow CI environments (e.g., when KinD clusters are running).
    /// </param>
    internal static Hex1bTerminalInputSequenceBuilder DeclineAgentInitPrompt(
        this Hex1bTerminalInputSequenceBuilder builder,
        TimeSpan? timeout = null)
    {
        var agentInitPrompt = new CellPatternSearcher()
            .Find("configure AI agent environments");

        return builder
            .WaitUntil(s => agentInitPrompt.Search(s).Count > 0, timeout ?? TimeSpan.FromSeconds(120))
            .Wait(500)
            .Type("n");
    }

    /// <summary>
    /// Handles the agent init confirmation prompt that appears after <c>aspire init</c> or <c>aspire new</c>,
    /// then waits for the shell success prompt. Supports CLI versions both with and without agent init chaining.
    /// Replaces the separate <c>.DeclineAgentInitPrompt().WaitForSuccessPrompt(counter)</c> pattern.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="counter">The sequence counter for prompt detection.</param>
    /// <param name="timeout">
    /// Maximum time to wait for the command to complete. Defaults to 500 seconds to match
    /// <see cref="WaitForSuccessPrompt"/>. Must be long enough to cover project creation time.
    /// </param>
    internal static Hex1bTerminalInputSequenceBuilder DeclineAgentInitPrompt(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(500);

        var agentInitPrompt = new CellPatternSearcher()
            .Find("configure AI agent environments");

        return builder
            // Wait for either the agent init prompt (new CLI) or the success prompt (old CLI).
            // Uses the full timeout since aspire new project creation can take minutes.
            .WaitUntil(s =>
            {
                if (agentInitPrompt.Search(s).Count > 0)
                {
                    return true;
                }
                var successSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");
                return successSearcher.Search(s).Count > 0;
            }, effectiveTimeout)
            .Wait(500)
            // Type 'n' unconditionally:
            // - Agent init: declines the prompt, CLI exits, success prompt appears
            // - No agent init: 'n' is typed at bash but not submitted; the existing success prompt remains and Ctrl+U clears the pending input
            .Type("n")
            // Wait for the aspire command's success prompt (already on screen or appears after decline)
            .WaitUntil(s =>
            {
                var successSearcher = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");
                return successSearcher.Search(s).Count > 0;
            }, effectiveTimeout)
            .Ctrl().Key(Hex1bKey.U)
            // Increment counter correctly for both cases:
            // - Agent init: one increment for the aspire command
            // - No agent init: one increment for the aspire command
            .WaitUntil(_ =>
            {
                counter.Increment();
                return true;
            }, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Runs <c>aspire new</c> interactively, selecting the specified template and responding to all prompts.
    /// This centralizes the multi-step interactive flow so that changes to <c>aspire new</c> prompts
    /// only need to be updated in one place instead of across every E2E test.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="projectName">The project name to enter at the prompt.</param>
    /// <param name="counter">The sequence counter for prompt detection.</param>
    /// <param name="template">The template to select. Defaults to <see cref="AspireTemplate.Starter"/>.</param>
    /// <param name="useRedisCache">
    /// Whether to enable Redis Cache. Defaults to <c>true</c> (the <c>aspire new</c> default).
    /// Only applies to templates that show the Redis prompt (Starter, JsReact, PythonReact).
    /// </param>
    /// <returns>The builder for chaining.</returns>
    internal static Hex1bTerminalInputSequenceBuilder AspireNew(
        this Hex1bTerminalInputSequenceBuilder builder,
        string projectName,
        SequenceCounter counter,
        AspireTemplate template = AspireTemplate.Starter,
        bool useRedisCache = true)
    {
        var templateTimeout = TimeSpan.FromSeconds(60);

        // Wait for the template selection list to appear.
        // The first item "> Starter App" is always highlighted initially.
        var waitingForTemplateList = new CellPatternSearcher()
            .Find("> Starter App");

        var waitingForProjectNamePrompt = new CellPatternSearcher()
            .Find("Enter the project name");

        var waitingForOutputPathPrompt = new CellPatternSearcher()
            .Find("Enter the output path");

        var waitingForAppHostLanguagePrompt = new CellPatternSearcher()
            .Find("Which language would you like to use?");

        var waitingForUrlsPrompt = new CellPatternSearcher()
            .Find("Use *.dev.localhost URLs");

        // Step 1: Type aspire new and wait for the template list
        builder.Type("aspire new")
            .Enter()
            .WaitUntil(s => waitingForTemplateList.Search(s).Count > 0, templateTimeout);

        // Step 2: Navigate to and select the desired template
        switch (template)
        {
            case AspireTemplate.Starter:
                builder.Enter(); // First option, no navigation needed
                break;

            case AspireTemplate.JsReact:
                var jsReactSelected = new CellPatternSearcher()
                    .Find("> Starter App (ASP.NET Core/React, C# AppHost)");
                builder.Key(Hex1bKey.DownArrow)
                    .WaitUntil(s => jsReactSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;

            case AspireTemplate.ExpressReact:
                var expressReactSelected = new CellPatternSearcher()
                    .Find("> Starter App (Express/React, TypeScript AppHost)");
                builder.Key(Hex1bKey.DownArrow)
                    .Key(Hex1bKey.DownArrow)
                    .WaitUntil(s => expressReactSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;

            case AspireTemplate.PythonReact:
                var pythonReactSelected = new CellPatternSearcher()
                    .Find("> Starter App (FastAPI/React, TypeScript AppHost)");
                builder.Key(Hex1bKey.DownArrow)
                    .Key(Hex1bKey.DownArrow)
                    .Key(Hex1bKey.DownArrow)
                    .WaitUntil(s => pythonReactSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;

            case AspireTemplate.EmptyAppHost:
                builder.Type("Empty AppHost")
                    .Enter()
                    .WaitUntil(s => waitingForAppHostLanguagePrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10))
                    .Enter(); // Select C# language
                break;

            case AspireTemplate.TypeScriptEmptyAppHost:
                var typeScriptEmptyAppHostSelected = new CellPatternSearcher()
                    .Find("> Empty (TypeScript AppHost)");
                builder.Type("Empty (TypeScript")
                    .WaitUntil(s => typeScriptEmptyAppHostSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;

            case AspireTemplate.JavaEmptyAppHost:
                var javaEmptyAppHostSelected = new CellPatternSearcher()
                    .Find("> Empty (Java AppHost)");
                builder.Type("Empty (Java AppHost)")
                    .WaitUntil(s => javaEmptyAppHostSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;

            case AspireTemplate.PythonEmptyAppHost:
                var pythonEmptyAppHostSelected = new CellPatternSearcher()
                    .Find("> Empty (Python AppHost)");
                builder.Type("Empty (Python AppHost)")
                    .WaitUntil(s => pythonEmptyAppHostSelected.Search(s).Count > 0, TimeSpan.FromSeconds(5))
                    .Enter();
                break;
        }

        // Step 3: Enter project name
        builder.WaitUntil(s => waitingForProjectNamePrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10))
            .Type(projectName)
            .Enter();

        // Step 4: Accept default output path
        builder.WaitUntil(s => waitingForOutputPathPrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10))
            .Enter();

        // Step 5: URLs prompt (all templates have this)
        builder.WaitUntil(s => waitingForUrlsPrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10))
            .Enter(); // Accept default "No"

        // Step 6: Redis prompt (only Starter, JsReact, PythonReact)
        if (template is AspireTemplate.Starter or AspireTemplate.JsReact or AspireTemplate.PythonReact)
        {
            var waitingForRedisPrompt = new CellPatternSearcher()
                .Find("Use Redis Cache");
            builder.WaitUntil(s => waitingForRedisPrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10));

            if (!useRedisCache)
            {
                builder.Type("n");
            }
            else
            {
                builder.Enter();
            }
        }

        // Step 7: Test project prompt (only Starter)
        if (template is AspireTemplate.Starter)
        {
            var waitingForTestPrompt = new CellPatternSearcher()
                .Find("Do you want to create a test project?");
            builder.WaitUntil(s => waitingForTestPrompt.Search(s).Count > 0, TimeSpan.FromSeconds(10))
                .Enter(); // Accept default "No"
        }

        // Step 8: Decline the agent init prompt and wait for success
        return builder.DeclineAgentInitPrompt(counter);
    }

    /// <summary>
    /// Runs <c>aspire init --language csharp</c> and handles the NuGet.config and agent init prompts.
    /// Explicitly waits for the NuGet.config prompt (or init completion) rather than using a blind timer,
    /// then declines the agent init prompt so the command exits cleanly.
    /// </summary>
    internal static Hex1bTerminalInputSequenceBuilder AspireInit(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter)
    {
        var waitingForNuGetConfigPrompt = new CellPatternSearcher()
            .Find("NuGet.config");

        var waitingForInitComplete = new CellPatternSearcher()
            .Find("Aspire initialization complete");

        return builder
            .Type("aspire init --language csharp")
            .Enter()
            // NuGet.config prompt may or may not appear depending on environment.
            // Wait for either the NuGet.config prompt or init completion.
            .WaitUntil(s => waitingForNuGetConfigPrompt.Search(s).Count > 0
                || waitingForInitComplete.Search(s).Count > 0, TimeSpan.FromMinutes(2))
            .Enter()  // Dismiss NuGet.config prompt if present
            .WaitUntil(s => waitingForInitComplete.Search(s).Count > 0, TimeSpan.FromMinutes(2))
            .DeclineAgentInitPrompt()
            .WaitForSuccessPrompt(counter, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Installs the Aspire CLI Bundle from a specific pull request's artifacts.
    /// The bundle is a self-contained distribution that includes:
    /// - Native AOT Aspire CLI
    /// - .NET runtime
    /// - Dashboard, DCP, AppHost Server (for polyglot apps)
    /// This is required for polyglot (TypeScript, Python) AppHost scenarios which
    /// cannot use SDK-based fallback mode.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="prNumber">The pull request number to download from.</param>
    /// <param name="counter">The sequence counter for prompt detection.</param>
    /// <returns>The builder for chaining.</returns>
    internal static Hex1bTerminalInputSequenceBuilder InstallAspireBundleFromPullRequest(
        this Hex1bTerminalInputSequenceBuilder builder,
        int prNumber,
        SequenceCounter counter)
    {
        // The install script may not be on main yet, so we need to fetch it from the PR's branch.
        // Use the PR head SHA (not branch ref) to avoid CDN caching on raw.githubusercontent.com
        // which can serve stale script content for several minutes after a push.
        string command;
        if (OperatingSystem.IsWindows())
        {
            // PowerShell: Get PR head SHA, then fetch and run install script from that SHA
            command = $"$ref = (gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha'); " +
                      $"iex \"& {{ $(irm https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.ps1) }} {prNumber}\"";
        }
        else
        {
            // Bash: Get PR head SHA, then fetch and run install script from that SHA
            command = $"ref=$(gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha') && " +
                      $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {prNumber}";
        }

        return builder
            .Type(command)
            .Enter()
            .WaitForSuccessPromptFailFast(counter, TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Sources the Aspire Bundle environment after installation.
    /// Adds both the bundle's bin/ directory and root directory to PATH so the CLI
    /// is discoverable regardless of which version of the install script ran
    /// (the script is fetched from raw.githubusercontent.com which has CDN caching).
    /// The CLI auto-discovers bundle components (runtime, dashboard, DCP, AppHost server)
    /// in the parent directory via relative path resolution.
    /// </summary>
    /// <param name="builder">The sequence builder.</param>
    /// <param name="counter">The sequence counter for prompt detection.</param>
    /// <returns>The builder for chaining.</returns>
    internal static Hex1bTerminalInputSequenceBuilder SourceAspireBundleEnvironment(
        this Hex1bTerminalInputSequenceBuilder builder,
        SequenceCounter counter)
    {
        if (OperatingSystem.IsWindows())
        {
            // PowerShell environment setup for bundle
            return builder
                .Type("$env:PATH=\"$HOME\\.aspire\\bin;$HOME\\.aspire;$env:PATH\"; $env:ASPIRE_PLAYGROUND='true'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='true'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='true'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'")
                .Enter()
                .WaitForSuccessPrompt(counter);
        }

        // Bash environment setup for bundle
        // Add both ~/.aspire/bin (new layout) and ~/.aspire (old layout) to PATH
        // The install script is downloaded from raw.githubusercontent.com which has CDN caching,
        // so the old version may still be served for a while after push.
        return builder
            .Type("export PATH=~/.aspire/bin:~/.aspire:$PATH ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false")
            .Enter()
            .WaitForSuccessPrompt(counter);
    }
}
