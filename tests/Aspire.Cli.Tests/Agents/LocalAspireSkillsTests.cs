// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class LocalAspireSkillsTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData("GitHub Copilot", ".github")]
    [InlineData("GitHub Copilot", ".agents")]
    [InlineData("Claude Code", ".claude")]
    [InlineData("OpenCode", ".agents")]
    public async Task Find_ReportsKnownNamesWithoutInferringOwnershipOrModifyingFiles(string agent, string directory)
    {
        var path = Path.Combine(_context.Project.FullName, directory, "skills", "aspire", "SKILL.md");
        const string content = "---\nname: aspire\n---\nMy customized instructions.";
        await AgentConfigurationTestContext.WriteAsync(path, content);
        var empty = Path.Combine(_context.Project.FullName, directory, "skills", "aspire-init", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(empty, string.Empty);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, directory, "skills", "aspire-custom", "SKILL.md"), "unrelated");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, directory, "skills", "playwright-cli", "SKILL.md"), "unrelated");
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = Scan([_context.Clients.Single(scanner => scanner.DisplayName == agent)]);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { path, empty }.Order(AgentPath.Comparer), result.Files);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Empty(await File.ReadAllTextAsync(empty));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task Find_ChecksBothScopesAndDeduplicatesSharedLocations()
    {
        var project = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire-init", "SKILL.md");
        var user = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspireify", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(project, "project");
        await AgentConfigurationTestContext.WriteAsync(user, "user");

        var result = Scan([_context.Copilot, _context.OpenCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { project, user }.Order(AgentPath.Comparer), result.Files);
    }

    [Fact]
    public async Task Find_OnlyUsesTheSelectedAgentsLocationsAndHonorsOverrides()
    {
        var custom = Path.Combine(_context.Home.FullName, "claude-work");
        _context.SetVariable("CLAUDE_CONFIG_DIR", custom);
        var selected = Path.Combine(custom, "skills", "aspire-deployment", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(selected, "customized");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Home.FullName, ".claude", "skills", "aspire", "SKILL.md"), "old home");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, ".github", "skills", "aspire", "SKILL.md"), "other agent");

        var result = Scan([_context.ClaudeCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(selected, Assert.Single(result.Files));
    }

    [Fact]
    public async Task Find_RecognizesLegacyOpenCodeAndCustomUserLocations()
    {
        var custom = _context.Workspace.CreateDirectory("custom-opencode");
        _context.SetVariable("OPENCODE_CONFIG_DIR", custom.FullName);
        var paths = new[]
        {
            Path.Combine(_context.Project.FullName, ".opencode", "skill", "aspire", "SKILL.md"),
            Path.Combine(custom.FullName, "skills", "aspire-orchestration", "SKILL.md")
        };
        foreach (var path in paths)
        {
            await AgentConfigurationTestContext.WriteAsync(path, "local");
        }

        var result = Scan([_context.OpenCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(paths.Order(AgentPath.Comparer), result.Files);
    }

    [Fact]
    public async Task Find_DoesNotReadPluginCachesOrNestedUnrelatedProjects()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.CopilotDirectory, "installed-plugins", "aspire", "skills", "aspire", "SKILL.md"), "installed");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, "unrelated", ".agents", "skills", "aspire", "SKILL.md"), "unrelated");

        var result = Scan(_context.Clients);

        Assert.Empty(result.Files);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("GitHub Copilot", ".github")]
    [InlineData("Claude Code", ".claude")]
    [InlineData("OpenCode", ".opencode")]
    [InlineData("OpenCode", ".claude")]
    [InlineData("OpenCode", ".agents")]
    public async Task Find_InspectsOnlyActiveAncestorsWithinTheWorkspace(string agent, string directory)
    {
        var parent = _context.Project.CreateSubdirectory("apps");
        var working = parent.CreateSubdirectory("active");
        var paths = new[] { _context.Project, parent, working }
            .Select(root => Path.Combine(root.FullName, directory, "skills", "aspire", "SKILL.md")).ToArray();
        foreach (var path in paths)
        {
            await AgentConfigurationTestContext.WriteAsync(path, "active local skill");
        }
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(parent.FullName, "sibling", directory, "skills", "aspire", "SKILL.md"), "unrelated");
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.Workspace.Path, directory, "skills", "aspire", "SKILL.md"), "outside the workspace");
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = LocalAspireSkills.Find(working, _context.Project,
            [_context.Clients.Single(scanner => scanner.DisplayName == agent)], TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal(paths.Order(AgentPath.Comparer), result.Files);
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task Find_OutsideWorkingDirectoryUsesOnlyTheSelectedWorkspace()
    {
        var outside = _context.Workspace.CreateDirectory("outside");
        var selected = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(selected, "selected");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(outside.FullName, ".agents", "skills", "aspire", "SKILL.md"), "outside");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Workspace.Path, ".agents", "skills", "aspire", "SKILL.md"), "ancestor");

        var result = LocalAspireSkills.Find(outside, _context.Project, [_context.OpenCode],
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal(selected, Assert.Single(result.Files));
    }

    [Fact]
    public async Task Find_ReportsInspectionErrorsRatherThanClaimingNoConflicts()
    {
        var path = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Directory.CreateDirectory(path);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, ".agents", "skills", "aspireify", "SKILL.md"), "readable");

        var result = Scan([_context.Copilot]);

        Assert.Single(result.Files);
        Assert.Contains(path, Assert.Single(result.Errors));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Find_DeduplicatesSymlinkAliasesWithoutChangingThem()
    {
        var skills = _context.Project.CreateSubdirectory(Path.Combine(".agents", "skills"));
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skills.FullName, "aspire", "SKILL.md"), "local");
        var claude = _context.Project.CreateSubdirectory(".claude");
        var link = Path.Combine(claude.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, skills.FullName);

        var result = Scan([_context.Copilot, _context.ClaudeCode]);

        Assert.Single(result.Files);
        Assert.Empty(result.Errors);
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void Find_CancellationPropagates()
    {
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LocalAspireSkills.Find(_context.Project, _context.Project, _context.Clients, new CancellationToken(canceled: true)));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    private LocalAspireSkillScan Scan(IEnumerable<IAgentClient> agents)
        => LocalAspireSkills.Find(_context.Project, _context.Project, agents, TestContext.Current.CancellationToken);

    public void Dispose() => _context.Dispose();
}
