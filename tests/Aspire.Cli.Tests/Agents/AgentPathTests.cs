// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

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

    [Fact]
    public void CopilotDirectory_ExpandsHomeOverride()
    {
        _context.SetVariable("COPILOT_HOME", @"~\custom-config");
        var expected = Path.Combine(_context.Home.FullName, "custom-config");

        Assert.Equal(expected, _context.CopilotDirectory);
    }

    [Fact]
    public void Absolute_DoesNotInterpretAnotherUsersTildeAsTheCurrentHome()
    {
        Assert.Equal(Path.Combine(_context.Project.FullName, "~other-user"), AgentPath.Expand("~other-user", _context.ExecutionContext));
    }

    [Fact]
    public void Resolve_RetainsMultipleMissingPathSegments()
    {
        var suffix = Path.Combine("plugins", "pending", "settings.json");

        Assert.Equal(Path.Combine(AgentPath.Resolve(_context.Project.FullName), suffix),
            AgentPath.Resolve(Path.Combine(_context.Project.FullName, suffix)));
    }

    [Fact]
    public async Task Resolve_DeduplicatesProjectAndUserDirectorySymlinkAliases()
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_context.Project.FullName, ".github", "copilot"));
        var alias = Path.Combine(_context.Home.FullName, "copilot-link");
        TestSymlinkHelper.TryCreateSymlink(alias, projectDirectory.FullName);
        _context.SetVariable("COPILOT_HOME", alias);

        Assert.Equal(AgentPath.Resolve(projectDirectory.FullName), AgentPath.Resolve(alias));
        var result = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.Copilot]).DefaultTimeout();

        var target = Assert.Single(result);
        Assert.Equal(AgentConfigurationStatus.Configured, target.Status);
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
        Assert.True(File.Exists(Path.Combine(projectDirectory.FullName, "settings.json")));
    }

    [Fact]
    public async Task Resolve_FileSymlinksShareOnePhysicalIdentity()
    {
        var file = Path.Combine(_context.Project.FullName, "settings.json");
        var alias = Path.Combine(_context.Project.FullName, "alias.json");
        await File.WriteAllTextAsync(file, "{}").DefaultTimeout();
        TestSymlinkHelper.TryCreateSymlink(alias, file, isDirectory: false);

        Assert.Equal(AgentPath.Resolve(file), AgentPath.Resolve(alias));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_RejectsDanglingFileAndDirectoryLinks(bool directory)
    {
        var missing = Path.Combine(_context.Project.FullName, "missing-target");
        var alias = Path.Combine(_context.Project.FullName, "dangling-link");
        TestSymlinkHelper.TryCreateSymlink(alias, missing, isDirectory: directory);

        Assert.Throws<AgentConfigurationException>(() => AgentPath.Resolve(alias));
        Assert.False(Path.Exists(missing));
    }

    [Fact]
    public async Task Resolve_DeduplicatesCaseAliasesForMissingSettingsOnCaseInsensitiveMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("This regression covers case-insensitive macOS filesystems.");
        }

        var projectDirectory = Directory.CreateDirectory(Path.Combine(_context.Project.FullName, ".github", "copilot"));
        var caseAlias = Path.Combine(_context.Project.FullName, ".GITHUB", "COPILOT");
        if (!Directory.Exists(caseAlias))
        {
            Assert.Skip("The test volume is case-sensitive.");
        }

        _context.SetVariable("COPILOT_HOME", caseAlias);
        var projectFile = Path.Combine(projectDirectory.FullName, "settings.json");
        var aliasFile = Path.Combine(caseAlias, "settings.json");
        Assert.False(File.Exists(projectFile));
        Assert.Equal(AgentPath.Resolve(projectFile), AgentPath.Resolve(aliasFile));

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.Copilot]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.True(File.Exists(projectFile));
        Assert.Equal(AgentPath.Resolve(projectFile), AgentPath.Resolve(Path.Combine(caseAlias, "SETTINGS.JSON")));
    }

    [Fact]
    public async Task Resolve_DoesNotCollapseDistinctNamesOnCaseSensitiveVolumes()
    {
        var original = Path.Combine(_context.Project.FullName, "Settings.json");
        var alternate = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(original, "{}").DefaultTimeout();
        if (File.Exists(alternate))
        {
            Assert.Skip("The test volume is case-insensitive.");
        }

        await File.WriteAllTextAsync(alternate, """{"different":true}""").DefaultTimeout();

        Assert.NotEqual(AgentPath.Resolve(original), AgentPath.Resolve(alternate));
    }

    public void Dispose() => _context.Dispose();
}
