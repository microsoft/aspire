// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.Agents;

public class LocalAspireSkillsTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task Find_RecognizesLegacySkillDirectoriesWithoutModifyingFiles()
    {
        string[] names =
        [
            "aspire",
            "aspire-deployment",
            "aspire-init",
            "aspire-monitoring",
            "aspire-orchestration",
            "aspire-project-v2-migration",
            "aspireify"
        ];
        var skills = Path.Combine(_context.Project.FullName, ".agents", "skills");
        foreach (var name in names)
        {
            await AgentConfigurationTestContext.WriteAsync(Path.Combine(skills, name, "references", "notes.md"), "local skill");
        }
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skills, "aspire-custom", "SKILL.md"), "unrelated");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skills, "playwright-cli", "SKILL.md"), "unrelated");
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = Find(_context.Project, _context.Project);

        Assert.Equal(
            names.Select(name => (Path.Combine(skills, name), AgentConfigurationScope.Project)).OrderBy(entry => entry.Item1, StringComparers.FileSystemPath),
            result);
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData(".agents", "skills")]
    [InlineData(".claude", "skills")]
    [InlineData(".github", "skills")]
    [InlineData(".opencode", "skill")]
    public async Task Find_InspectsOnlyActiveAncestorsWithinTheWorkspace(string directory, string skills)
    {
        var parent = _context.Project.CreateSubdirectory("apps");
        var working = parent.CreateSubdirectory("active");
        var paths = new[] { _context.Project, parent, working }
            .Select(root => Path.Combine(root.FullName, directory, skills, "aspire")).ToArray();
        foreach (var path in paths)
        {
            await AgentConfigurationTestContext.WriteAsync(Path.Combine(path, "SKILL.md"), "active local skill");
        }
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(parent.FullName, "sibling", directory, skills, "aspire", "SKILL.md"), "unrelated");
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.Workspace.Path, directory, skills, "aspire", "SKILL.md"), "outside the workspace");

        var result = Find(working, _context.Project);

        Assert.Equal(
            paths.Select(path => (path, AgentConfigurationScope.Project)).OrderBy(entry => entry.path, StringComparers.FileSystemPath),
            result);
    }

    [Fact]
    public async Task Find_TagsUserLevelCopiesAsGlobalAndIgnoresOtherUserLocations()
    {
        var project = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire-init");
        var user = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspireify");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(project, "SKILL.md"), "project");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(user, "SKILL.md"), "user");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Home.FullName, ".claude", "skills", "aspire", "SKILL.md"), "claude");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.CopilotDirectory, "skills", "aspire", "SKILL.md"), "copilot");

        var result = Find(_context.Project, _context.Project);

        Assert.Equal(
            new[] { (project, AgentConfigurationScope.Project), (user, AgentConfigurationScope.Global) }
                .OrderBy(entry => entry.Item1, StringComparers.FileSystemPath),
            result);
    }

    [Fact]
    public async Task Find_WorkspaceAtHomeReportsSharedLocationUnderBothScopes()
    {
        var skill = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspire");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skill, "SKILL.md"), "shared");

        var result = Find(_context.Home, _context.Home);

        Assert.Equal([(skill, AgentConfigurationScope.Project), (skill, AgentConfigurationScope.Global)], result);
    }

    [Fact]
    public async Task Find_DoesNotReadPluginCachesOrNestedUnrelatedProjects()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.CopilotDirectory, "installed-plugins", "aspire", "skills", "aspire", "SKILL.md"), "installed");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, "unrelated", ".agents", "skills", "aspire", "SKILL.md"), "unrelated");

        Assert.Empty(Find(_context.Project, _context.Project));
    }

    [Fact]
    public async Task Find_OutsideWorkingDirectoryUsesOnlyTheSelectedWorkspace()
    {
        var outside = _context.Workspace.CreateDirectory("outside");
        var selected = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(selected, "SKILL.md"), "selected");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(outside.FullName, ".agents", "skills", "aspire", "SKILL.md"), "outside");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Workspace.Path, ".agents", "skills", "aspire", "SKILL.md"), "ancestor");

        var result = Find(outside, _context.Project);

        Assert.Equal((selected, AgentConfigurationScope.Project), Assert.Single(result));
    }

    [Fact]
    public void Find_CancellationPropagates()
    {
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LocalAspireSkills.Find(_context.Project, _context.Project, _context.Home, new CancellationToken(canceled: true)));
    }

    private IReadOnlyList<(string Directory, AgentConfigurationScope Scope)> Find(DirectoryInfo working, DirectoryInfo workspaceRoot)
        => LocalAspireSkills.Find(working, workspaceRoot, _context.Home, TestContext.Current.CancellationToken)
            .Select(static copy => (copy.Directory, copy.Scope))
            .ToArray();

    public void Dispose() => _context.Dispose();
}
