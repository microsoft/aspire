// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression test for https://github.com/microsoft/aspire/issues/15986.
/// </summary>
/// <remarks>
/// <para>
/// Verifies that the single-file C# AppHost dropped by interactive <c>aspire init</c>
/// can be launched directly with <c>dotnet run apphost.cs</c>.
/// </para>
/// <para>
/// Before the fix, <c>aspire init</c> wrote <c>apphost.cs</c>, <c>aspire.config.json</c>,
/// and <c>NuGet.config</c> but skipped <c>apphost.run.json</c>. The .NET file-based
/// runner only honours <c>[file].run.json</c> for launch profiles, so without it the
/// AppHost crashed at startup with:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><c>Failed to configure dashboard resource because ASPNETCORE_URLS environment variable was not set.</c></description>
///   </item>
///   <item>
///     <description><c>Failed to configure dashboard resource because ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL and ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL environment variables are not set.</c></description>
///   </item>
/// </list>
/// </remarks>
public sealed class SingleFileAppHostInitDotnetRunTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task AspireInitSingleFileAppHostCanRunWithDotnetRunFile()
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

        // Run aspire init without --language so the interactive language prompt is shown,
        // then accept the default '> C#' selection.
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> C#").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(30),
            description: "language selection prompt with default '> C#'");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Sanity-check the host-side files dropped by aspire init.
        var appHostCs = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var aspireConfigJson = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        Assert.True(File.Exists(appHostCs), $"Expected apphost.cs to exist at: {appHostCs}");
        Assert.True(File.Exists(aspireConfigJson), $"Expected aspire.config.json to exist at: {aspireConfigJson}");

        // The actual regression assertion: dotnet run apphost.cs must be able to start the AppHost.
        // Without apphost.run.json, the launch profile env vars (ASPNETCORE_URLS,
        // ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL, ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL) are missing
        // and the dashboard resource fails configuration.
        await auto.TypeAsync("dotnet run apphost.cs");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Failed to configure dashboard resource"))
            {
                throw new InvalidOperationException(
                    "dotnet run apphost.cs crashed because the dashboard environment variables were not set. "
                    + "This is the regression tracked by https://github.com/microsoft/aspire/issues/15986: "
                    + "aspire init must drop apphost.run.json alongside apphost.cs so the .NET file-based "
                    + "runner can apply the launch profile.");
            }

            return s.ContainsText("Distributed application started.");
        }, timeout: TimeSpan.FromMinutes(5), description: "wait for apphost dashboard startup or known failure");

        // Stop the AppHost cleanly so the prompt comes back.
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromMinutes(1));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
