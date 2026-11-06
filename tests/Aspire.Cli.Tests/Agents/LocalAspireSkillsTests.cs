// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.Agents;

public class LocalAspireSkillsTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task Find_RecognizesLegacySkillsWithoutCreatingABundleOrCache()
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
        var paths = names.Select(name => Path.Combine(_context.Project.FullName, ".agents", "skills", name, "SKILL.md")).ToArray();
        foreach (var path in paths)
        {
            await AgentConfigurationTestContext.WriteAsync(path, "local skill");
        }
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.False(Directory.Exists(_context.ExecutionContext.CacheDirectory.FullName));

        var result = Scan([_context.Copilot]);

        Assert.Empty(result.Errors);
        Assert.Equal(paths.Order(StringComparers.FileSystemPath), result.Files);
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("GitHub Copilot", ".github")]
    [InlineData("GitHub Copilot", ".agents")]
    [InlineData("GitHub Copilot", ".claude")]
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
        Assert.Equal(new[] { path, empty }.Order(StringComparers.FileSystemPath), result.Files);
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
        Assert.Equal(new[] { project, user }.Order(StringComparers.FileSystemPath), result.Files);
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
        Assert.Equal(paths.Order(StringComparers.FileSystemPath), result.Files);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Find_RecognizesConfiguredOpenCodeSkillRootsWithoutChangingFiles(bool array, bool global)
    {
        var configPath = global
            ? Path.Combine(_context.OpenCodeDirectory, "opencode.jsonc")
            : Path.Combine(_context.Project.FullName, ".opencode", "opencode.jsonc");
        var paths = new[]
        {
            Path.Combine(_context.Project.FullName, "team-skills", "aspire", "SKILL.md"),
            Path.Combine(_context.Home.FullName, "shared-skills", "aspireify", "SKILL.md")
        };
        var config = array
            ? """{"skills":["./team-skills","~/shared-skills","https://example.test/skills/"]}"""
            : """{"skills":{"paths":["./team-skills","~/shared-skills"],"urls":["https://example.test/skills/"]}}""";
        await AgentConfigurationTestContext.WriteAsync(configPath, config);
        foreach (var path in paths)
        {
            await AgentConfigurationTestContext.WriteAsync(path, "local");
        }

        var result = Scan([_context.OpenCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(paths.Order(StringComparers.FileSystemPath), result.Files);
        Assert.Equal(config, await File.ReadAllTextAsync(configPath));
        foreach (var path in paths)
        {
            Assert.Equal("local", await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"skills":{"paths":[true]}}""")]
    [InlineData("""{"skills":[false]}""")]
    public async Task Find_ReportsInvalidOpenCodeSkillConfiguration(string config)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, config);

        var result = Scan([_context.OpenCode]);

        Assert.Empty(result.Files);
        Assert.Contains("OpenCode", Assert.Single(result.Errors));
        Assert.False(result.HasFileErrors);
        Assert.Equal(config, await File.ReadAllTextAsync(path));
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
    [InlineData("GitHub Copilot", ".claude")]
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
        Assert.Equal(paths.Order(StringComparers.FileSystemPath), result.Files);
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
        Assert.True(result.HasFileErrors);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Find_CancellationPropagates()
    {
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LocalAspireSkills.Find(_context.Project, _context.Project, _context.Clients, new CancellationToken(canceled: true)));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    private (IReadOnlyList<string> Files, IReadOnlyList<string> Errors, bool HasFileErrors) Scan(IEnumerable<IAgentClient> agents)
        => LocalAspireSkills.Find(_context.Project, _context.Project, agents, TestContext.Current.CancellationToken);

    public void Dispose() => _context.Dispose();
}
