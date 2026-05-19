// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke tests for the Aspire CLI installed via <c>dotnet tool install --global Aspire.Cli</c>.
/// Unlike the general smoke tests that use <see cref="CliInstallStrategy.Detect"/>, these tests
/// explicitly construct a DotnetTool strategy to validate the dotnet tool distribution channel.
/// <para>
/// The strategy is resolved from (in priority order):
/// <list type="number">
///   <item><c>ASPIRE_E2E_DOTNET_TOOL_SOURCE</c> — install from local nupkg directory</item>
///   <item><c>BUILT_NUGETS_PATH</c> — auto-discover from CI-built nupkgs directory</item>
///   <item>Published NuGet feed when <c>ASPIRE_E2E_VERSION</c> or <c>ASPIRE_E2E_QUALITY</c> is set</item>
/// </list>
/// </para>
/// </summary>
public sealed class DotnetToolSmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunAspireStarterProject()
    {
        var strategy = GetDotnetToolStrategy();

        output.WriteLine($"DotnetTool strategy resolved: {strategy}");

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI via dotnet tool
        await auto.InstallAspireCliAsync(strategy, counter);

        // Verify the tool is installed via dotnet tool list
        await auto.TypeAsync("dotnet tool list -g");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire.cli", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify aspire is accessible from the dotnet tools path
        await auto.TypeAsync("command -v aspire");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(".dotnet/tools/aspire", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify self-update uses the dotnet tool update path for dotnet tool installs.
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("dotnet tool update -g Aspire.Cli", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the installed version matches expectations
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();

        if (strategy.Version is not null)
        {
            // When a specific version was requested, verify it was actually installed
            output.WriteLine($"Verifying installed version matches expected: {strategy.Version}");
            await auto.WaitUntilTextAsync(strategy.Version, timeout: TimeSpan.FromSeconds(10));
        }

        await auto.WaitForSuccessPromptAsync(counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireToolApp", counter);

        // Run the project with aspire run
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException(
                    "Unexpected apphost selection prompt detected! " +
                    "This indicates multiple apphosts were incorrectly detected.");
            }

            // Dotnet tool smoke tests can run against older published packages that used different AppHost casing.
            return s.GetScreenText().Contains("Press CTRL+C to stop the AppHost and exit.", StringComparison.OrdinalIgnoreCase);
        }, timeout: TimeSpan.FromMinutes(2), description: "Press CTRL+C message (aspire run started)");

        // Stop the running apphost with Ctrl+C
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Explicitly constructs a DotnetTool strategy from env vars, bypassing <see cref="CliInstallStrategy.Detect"/>.
    /// Checks (in order):
    /// <list type="number">
    ///   <item><c>ASPIRE_E2E_DOTNET_TOOL_SOURCE</c> — explicit local nupkg directory</item>
    ///   <item><c>BUILT_NUGETS_PATH</c> — auto-discover from CI-built nupkgs directory</item>
    ///   <item>Published NuGet feed when <c>ASPIRE_E2E_VERSION</c> or <c>ASPIRE_E2E_QUALITY</c> is set</item>
    /// </list>
    /// </summary>
    internal static CliInstallStrategy GetDotnetToolStrategy()
    {
        // 1. Explicit local nupkg source (user-provided)
        var source = Environment.GetEnvironmentVariable("ASPIRE_E2E_DOTNET_TOOL_SOURCE");

        if (!string.IsNullOrEmpty(source))
        {
            var version = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");

            return string.IsNullOrEmpty(version)
                ? CliInstallStrategy.FromDotnetToolLocalSource(source)
                : CliInstallStrategy.FromDotnetToolLocalSource(source, version);
        }

        // 2. Auto-discover from CI-built nupkgs (BUILT_NUGETS_PATH contains the tool nupkg
        //    directly since the dotnet tool is built as part of the normal package build)
        var builtNugetsPath = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");

        if (!string.IsNullOrEmpty(builtNugetsPath) && Directory.Exists(builtNugetsPath))
        {
            return CliInstallStrategy.FromDotnetToolLocalSource(builtNugetsPath);
        }

        // 3. Published NuGet feed. Require an explicit selector so local dotnet test runs
        //    don't silently install the latest package from the public feed.
        var requestedVersion = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            requestedVersion = null;
        }

        var quality = CliInstallStrategy.GetQualityFromEnvironment();
        if (requestedVersion is null && quality is null)
        {
            throw new InvalidOperationException(
                "Dotnet tool smoke tests require ASPIRE_E2E_QUALITY to select a daily published feed, " +
                "ASPIRE_E2E_VERSION to install a specific published version, " +
                "ASPIRE_E2E_DOTNET_TOOL_SOURCE to install from a local feed, " +
                "or BUILT_NUGETS_PATH containing exactly one Aspire.Cli pointer nupkg.");
        }

        return CliInstallStrategy.FromPublishedDotnetToolFeed(requestedVersion, quality);
    }
}
