// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;

namespace Aspire.Cli.Tests.Agents;

public class AgentPathTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/", "")]
    [InlineData(@"~\", "")]
    [InlineData("~/claude-work", "claude-work")]
    [InlineData(@"~\claude-work", "claude-work")]
    [InlineData(@"~\config\claude-work", "config/claude-work")]
    [InlineData(@"~/config\claude-work", "config/claude-work")]
    public void ClaudeDirectory_ExpandsHomeUsingEitherSeparator(string value, string relative)
    {
        _context.SetVariable("CLAUDE_CONFIG_DIR", value);
        var expected = relative.Length == 0
            ? _context.Home.FullName
            : Path.Combine(_context.Home.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, _context.ClaudeDirectory);
        Assert.Equal(Path.Combine(expected, ".claude.json"), ClaudeCodePaths.GetGlobalMcpConfigPath(_context.ExecutionContext, _context.Environment));
        Assert.Equal(expected, AgentPath.Expand(value, _context.ExecutionContext));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("~/custom-config")]
    [InlineData(@"~\custom-config")]
    public void CopilotDirectory_ExpandsHomeOverrideForNativeSettingsAndMcp(string value)
    {
        _context.SetVariable("COPILOT_HOME", value);
        var expected = Path.Combine(_context.Home.FullName, "custom-config");

        Assert.Equal(expected, _context.CopilotDirectory);
        Assert.Equal(Path.Combine(expected, "settings.json"),
            _context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project));
        Assert.Equal(Path.Combine(expected, "mcp-config.json"),
            _context.Copilot.GetMcpConfigPath(AgentConfigurationScope.Global, _context.Project));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public void Absolute_DoesNotInterpretAnotherUsersTildeAsTheCurrentHome()
    {
        Assert.Equal(Path.Combine(_context.Project.FullName, "~other-user"), AgentPath.Expand("~other-user", _context.ExecutionContext));
    }

    [Theory]
    [InlineData("project", ".agents/skills/aspire/SKILL.md")]
    [InlineData("home", "~/.agents/skills/aspire/SKILL.md")]
    [InlineData("project-other", null)]
    [InlineData("home-other", null)]
    public void GetDisplayPath_UsesWorkspaceAndHomeRoots(string directory, string? expected)
    {
        var path = Path.Combine(_context.Workspace.Path, directory, ".agents", "skills", "aspire", "SKILL.md");

        Assert.Equal(expected ?? path, AgentPath.GetDisplayPath(path, _context.Project, _context.ExecutionContext));
    }

    [Fact]
    public void GetDisplayPath_PrefersWorkspaceWhenItIsInsideHome()
    {
        var project = _context.Home.CreateSubdirectory("my-app");
        var path = Path.Combine(project.FullName, ".agents", "skills", "aspire", "SKILL.md");

        Assert.Equal(".agents/skills/aspire/SKILL.md", AgentPath.GetDisplayPath(path, project, _context.ExecutionContext));
    }

    [Fact]
    public void GetDisplayPath_WithExplicitScope_PreservesPathsOutsideThatScope()
    {
        var homePath = Path.Combine(_context.Home.FullName, "config.json");
        var projectPath = Path.Combine(_context.Project.FullName, "config.json");

        Assert.Equal(homePath, AgentPath.GetDisplayPath(homePath, AgentConfigurationScope.Project, _context.Project, _context.ExecutionContext));
        Assert.Equal(projectPath, AgentPath.GetDisplayPath(projectPath, AgentConfigurationScope.Global, _context.Project, _context.ExecutionContext));
    }

    public void Dispose() => _context.Dispose();
}
