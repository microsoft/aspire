// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for C# AppHost initialization.
/// </summary>
public sealed class CSharpInitTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileBasedInitRejectsTypeScriptWithoutChangingFiles(bool configuredLanguage)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var appDirectory = workspace.CreateDirectory("app");
        var existingFiles = new Dictionary<string, string>
        {
            [Path.Combine(appDirectory.FullName, "package.json")] = """{"name":"existing-web","private":true}""",
            [Path.Combine(appDirectory.FullName, "Incidental.slnx")] = "<Solution />"
        };
        if (configuredLanguage)
        {
            existingFiles[Path.Combine(appDirectory.FullName, "aspire.config.json")] = """{"appHost":{"language":"typescript/nodejs"}}""";
        }

        foreach (var (path, content) in existingFiles)
        {
            await File.WriteAllTextAsync(path, content);
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("cd app", counter);
        var languageArg = configuredLanguage ? string.Empty : " --language typescript";
        await auto.TypeAsync($"aspire init --file-based{languageArg} --non-interactive --suppress-agent-init > ../init-error.txt 2>&1");
        await auto.EnterAsync();

        var errorPrompt = new CellPatternSearcher()
            .Find($"[{counter.Value} ERR:1] $ ");
        var successPrompt = new CellPatternSearcher()
            .Find($"[{counter.Value} OK] $ ");
        var failedAsExpected = false;
        await auto.WaitUntilAsync(snapshot =>
        {
            failedAsExpected = errorPrompt.Search(snapshot).Count > 0;
            return failedAsExpected || successPrompt.Search(snapshot).Count > 0;
        }, timeout: TimeSpan.FromMinutes(2), description: "waiting for file-based initialization to reject TypeScript");
        counter.Increment();

        Assert.True(failedAsExpected, "Expected invalid-command exit code 1 for --file-based with TypeScript.");
        var errorOutput = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "init-error.txt"));
        Assert.Contains("The --file-based option requires C#. Select --language csharp or omit --file-based.", errorOutput);
        Assert.Equal(existingFiles.Keys.Order(), Directory.GetFiles(appDirectory.FullName, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, content) in existingFiles)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task FileBasedCSharpInitIgnoresIncidentalSolutions()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var toolsDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "tools"));
        var projectPath = Path.Combine(toolsDirectory.FullName, "Incidental.csproj");
        var existingFiles = new Dictionary<string, string>
        {
            [Path.Combine(workspace.WorkspaceRoot.FullName, "Incidental.sln")] = """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Global
                EndGlobal
                """,
            [Path.Combine(toolsDirectory.FullName, "Incidental.slnx")] = """
                <Solution>
                  <Project Path="Incidental.csproj" />
                </Solution>
                """,
            [projectPath] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """
        };
        foreach (var (path, content) in existingFiles)
        {
            await File.WriteAllTextAsync(path, content);
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Multiple solutions would require a selection without --file-based. Non-interactive
        // execution must succeed without selecting either the root or nested solution.
        await auto.RunCommandAsync(
            "aspire init --file-based --language csharp --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(2));

        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.run.json")));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")));
        Assert.NotNull(config);
        Assert.Equal("apphost.cs", config["appHost"]?["path"]?.GetValue<string>());
        Assert.Equal([projectPath], Directory.GetFiles(workspace.WorkspaceRoot.FullName, "*.csproj", SearchOption.AllDirectories));
        foreach (var (path, content) in existingFiles)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }

        // Use normal AppHost discovery to prove the root config selects the generated
        // AppHost despite the incidental projects, then verify it has no wired resources.
        await auto.AspireStartAsync(counter, additionalArgs: "--non-interactive");
        try
        {
            await auto.RunCommandAsync("aspire describe --format json > resources.json", counter);
            var description = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "resources.json")));
            Assert.NotNull(description);
            Assert.Empty(Assert.IsType<JsonArray>(description["resources"]));
        }
        finally
        {
            await auto.AspireStopAsync(counter);
        }
    }

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

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

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
    }
}
