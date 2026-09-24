// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class LocalAspireSkillsTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData("copilot", ".github")]
    [InlineData("copilot", ".agents")]
    [InlineData("claude", ".claude")]
    [InlineData("opencode", ".agents")]
    public async Task FindAsync_ReportsKnownNamesWithoutInferringOwnershipOrModifyingFiles(string agent, string directory)
    {
        var path = Path.Combine(_context.Project.FullName, directory, "skills", "aspire", "SKILL.md");
        const string content = "---\nname: aspire\n---\nMy customized instructions.";
        await AgentConfigurationTestContext.WriteAsync(path, content);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, directory, "skills", "aspire-custom", "SKILL.md"), "unrelated");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, directory, "skills", "playwright-cli", "SKILL.md"), "unrelated");
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = await ScanAsync([_context.Environments.Single(scanner => scanner.Id == agent)]);

        Assert.Empty(result.Errors);
        Assert.Equal(new LocalAspireSkill(path, AgentConfigurationScope.Project), Assert.Single(result.Files));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task FindAsync_ChecksBothScopesAndDeduplicatesSharedLocations()
    {
        var project = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire-init", "SKILL.md");
        var user = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspireify", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(project, "project");
        await AgentConfigurationTestContext.WriteAsync(user, "user");

        var result = await ScanAsync([_context.Copilot, _context.OpenCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { project, user }.Order(AgentPath.Comparer), result.Files.Select(file => file.Path));
        Assert.Equal(new[] { AgentConfigurationScope.User, AgentConfigurationScope.Project }.Order(), result.Files.Select(file => file.Scope).Order());
    }

    [Fact]
    public async Task FindAsync_OnlyUsesTheSelectedAgentsLocationsAndHonorsOverrides()
    {
        var custom = Path.Combine(_context.Home.FullName, "claude-work");
        _context.SetVariable("CLAUDE_CONFIG_DIR", custom);
        var selected = Path.Combine(custom, "skills", "aspire-deployment", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(selected, "customized");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Home.FullName, ".claude", "skills", "aspire", "SKILL.md"), "old home");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, ".github", "skills", "aspire", "SKILL.md"), "other agent");

        var result = await ScanAsync([_context.ClaudeCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(new LocalAspireSkill(selected, AgentConfigurationScope.User), Assert.Single(result.Files));
    }

    [Fact]
    public async Task FindAsync_RecognizesLegacyOpenCodeAndCustomUserLocations()
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

        var result = await ScanAsync([_context.OpenCode]);

        Assert.Empty(result.Errors);
        Assert.Equal(paths.Order(AgentPath.Comparer), result.Files.Select(file => file.Path));
    }

    [Fact]
    public async Task FindAsync_DoesNotReadPluginCachesOrNestedUnrelatedProjects()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.CopilotDirectory, "installed-plugins", "aspire", "skills", "aspire", "SKILL.md"), "installed");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, "unrelated", ".agents", "skills", "aspire", "SKILL.md"), "unrelated");

        var result = await ScanAsync(_context.Environments);

        Assert.Empty(result.Files);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task FindAsync_ReportsInspectionErrorsRatherThanClaimingNoConflicts()
    {
        var path = Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Directory.CreateDirectory(path);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, ".agents", "skills", "aspireify", "SKILL.md"), "readable");

        var result = await ScanAsync([_context.Copilot]);

        Assert.Single(result.Files);
        Assert.Contains(path, Assert.Single(result.Errors));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task FindAsync_DeduplicatesSymlinkAliasesWithoutChangingThem()
    {
        var skills = _context.Project.CreateSubdirectory(Path.Combine(".agents", "skills"));
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skills.FullName, "aspire", "SKILL.md"), "local");
        var claude = _context.Project.CreateSubdirectory(".claude");
        var link = Path.Combine(claude.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, skills.FullName);

        var result = await ScanAsync([_context.Copilot, _context.ClaudeCode]);

        Assert.Single(result.Files);
        Assert.Empty(result.Errors);
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public async Task FindAsync_CancellationPropagates()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalAspireSkills.FindAsync(_context.Project, _context.Environments, _context.ExecutionContext,
                _context.Environment, new CancellationToken(canceled: true)));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    private Task<LocalAspireSkillScan> ScanAsync(IEnumerable<IAgentEnvironmentScanner> agents)
        => LocalAspireSkills.FindAsync(_context.Project, agents, _context.ExecutionContext, _context.Environment, TestContext.Current.CancellationToken);

    public void Dispose() => _context.Dispose();
}
