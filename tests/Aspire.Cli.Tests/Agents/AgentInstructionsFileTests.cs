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

    private static string GetFilePath(TemporaryWorkspace workspace) =>
        Path.Combine(workspace.WorkspaceRoot.FullName, AgentInstructionsFile.FileName);

    private static async Task<string> ReadFileAsync(TemporaryWorkspace workspace) =>
        await File.ReadAllTextAsync(GetFilePath(workspace));
}
