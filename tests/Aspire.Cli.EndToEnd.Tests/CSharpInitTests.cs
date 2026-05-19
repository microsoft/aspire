// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression test for interactive <c>aspire init</c> with the default C# language selection.
/// Verifies that accepting the default prompt (rather than passing <c>--language csharp</c>) does not
/// produce a conflicting-files / overwrite error and that the expected output files are created.
/// </summary>
public sealed class CSharpInitTests(ITestOutputHelper output)
{
    /// <summary>
    /// Runs <c>aspire init</c> interactively, accepts the default <c>&gt; C#</c> selection from the
    /// language prompt, declines agent configuration, and verifies that both <c>apphost.cs</c> and
    /// <c>aspire.config.json</c> are created with the expected content.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task InteractiveCSharpInitCreatesExpectedFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Run aspire init without --language so the interactive language prompt is shown.
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();

        // Wait for the language selection prompt and confirm the default "> C#" choice.
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> C#").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(30),
            description: "language selection prompt with default '> C#'");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // --- Host-side file assertions ---
        var appHostCs = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var aspireConfigJson = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");

        Assert.True(File.Exists(appHostCs), $"Expected apphost.cs to exist at: {appHostCs}");
        Assert.True(File.Exists(aspireConfigJson), $"Expected aspire.config.json to exist at: {aspireConfigJson}");

        var configText = await File.ReadAllTextAsync(aspireConfigJson);
        var config = JsonNode.Parse(configText);
        Assert.NotNull(config);

        var appHostNode = config["appHost"];
        Assert.NotNull(appHostNode);

        var path = appHostNode!["path"]?.GetValue<string>();
        Assert.Equal("apphost.cs", path);

        var language = appHostNode["language"]?.GetValue<string>();
        Assert.NotNull(language);
        Assert.Contains("csharp", language, StringComparison.OrdinalIgnoreCase);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
