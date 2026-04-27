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
///   <item><c>ASPIRE_E2E_DOTNET_TOOL=true</c> — install from published NuGet feed (optionally with <c>ASPIRE_E2E_VERSION</c>)</item>
///   <item><c>BUILT_NUGETS_PATH</c> — auto-discover from CI-built nupkgs directory</item>
/// </list>
/// </para>
/// </summary>
public sealed class DotnetToolSmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DotnetToolInstall_CreateAndRunAspireStarterProject()
    {
        var strategy = DotnetToolE2ETestHelpers.ResolveRequiredStrategy();
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

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: TimeSpan.FromMinutes(2), description: "Press CTRL+C message (aspire run started)");

        // Stop the running apphost with Ctrl+C
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
