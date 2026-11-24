// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.Agents;

public class AgentInstructionsFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WriteAsync_CreatesFileWithStartFlowAndSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "My App.AppHost", "My App.AppHost.csproj");
        IReadOnlyList<AgentInstructionsSkill> skills = [.. AgentInstructionsFile.AspireSkillsPluginSkills, new("playwright-cli", "drive a browser.")];

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, skills, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Created, change);
        await Verify(await ReadFileAsync(workspace), "md");
    }

    [Fact]
    public async Task WriteAsync_AppendsSectionToExistingFileWithoutSection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        const string existing = "# Team rules\r\n\r\nRun `make lint` before committing.";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, [], CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Appended, change);
        Assert.Equal(
            existing + "\r\n\r\n" + AgentInstructionsFile.CreateSection("apphost.cs", [], "\r\n") + "\r\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_ReplacesOnlyTheAspireSection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var oldSection = AgentInstructionsFile.CreateSection("old.cs", [], "\n");
        await File.WriteAllTextAsync(GetFilePath(workspace), $"# Rules\n\n{oldSection}\n\n## More rules\nKeep tests green.\n");
        IReadOnlyList<AgentInstructionsSkill> skills = [new("dotnet-inspect", "query .NET APIs.")];

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, skills, CancellationToken.None);
        var unchanged = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, skills, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Updated, change);
        Assert.Equal(AgentInstructionsFileChange.Unchanged, unchanged);
        Assert.Equal(
            $"# Rules\n\n{AgentInstructionsFile.CreateSection("apphost.cs", skills, "\n")}\n\n## More rules\nKeep tests green.\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_LeavesFileWithUnterminatedSectionUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{AgentInstructionsFile.SectionStartMarkerPrefix} -->\nMy own notes.\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, [], CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.SkippedMalformedSection, change);
        Assert.Equal(existing, await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_PreservesTextAfterEndMarkerOnSameLine()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var oldSection = AgentInstructionsFile.CreateSection("old.cs", [], "\n");
        await File.WriteAllTextAsync(GetFilePath(workspace), $"# Rules\n\n{oldSection} Keep this team rule.\nMore rules.\n");

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, [], CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Updated, change);
        Assert.Equal(
            $"# Rules\n\n{AgentInstructionsFile.CreateSection("apphost.cs", [], "\n")} Keep this team rule.\nMore rules.\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_LeavesFileWithUnclosedEndMarkerUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{AgentInstructionsFile.SectionStartMarkerPrefix} -->\nOld section.\n{AgentInstructionsFile.SectionEndMarkerPrefix}\nMy own notes.\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, [], CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.SkippedMalformedSection, change);
        Assert.Equal(existing, await ReadFileAsync(workspace));
    }

    [Theory]
    [InlineData("src/App.AppHost/apphost.cs", "`aspire start --apphost src/App.AppHost/apphost.cs --non-interactive --isolated`")]
    [InlineData("src/My App/apphost.cs", "`aspire start --apphost 'src/My App/apphost.cs' --non-interactive --isolated`")]
    [InlineData("src/My $App/apphost.cs", "`aspire start --apphost 'src/My $App/apphost.cs' --non-interactive --isolated`")]
    [InlineData("src/Bob's App/apphost.cs", "`aspire start --apphost \"src/Bob's App/apphost.cs\" --non-interactive --isolated`")]
    [InlineData("src/Bob's $App/apphost.cs", "`aspire start --apphost 'src/Bob'\\''s $App/apphost.cs' --non-interactive --isolated`")]
    [InlineData("src/My`App/apphost.cs", "`` aspire start --apphost 'src/My`App/apphost.cs' --non-interactive --isolated ``")]
    public void CreateSection_QuotesAppHostPathAsOneLiteralShellArgument(string appHostPath, string expectedStartCommand)
    {
        var section = AgentInstructionsFile.CreateSection(appHostPath, [], "\n");

        Assert.Equal($"1. Start it: {expectedStartCommand}.", section.Split('\n').Single(line => line.StartsWith("1. ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetConfiguredAppHostPath_ReturnsNullForPathWithNulCharacter()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """{ "appHost": { "path": "bad\u0000.cs" } }""");

        Assert.Null(AgentInstructionsFile.GetConfiguredAppHostPath(workspace.WorkspaceRoot));
    }

    private static string GetFilePath(TemporaryWorkspace workspace) =>
        Path.Combine(workspace.WorkspaceRoot.FullName, AgentInstructionsFile.FileName);

    private static async Task<string> ReadFileAsync(TemporaryWorkspace workspace) =>
        await File.ReadAllTextAsync(GetFilePath(workspace));
}
