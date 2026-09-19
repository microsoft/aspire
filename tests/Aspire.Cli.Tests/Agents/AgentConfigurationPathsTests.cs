// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentConfigurationPathsTests(ITestOutputHelper output)
{
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
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("CLAUDE_CONFIG_DIR", value);
        var expected = relative.Length == 0
            ? context.Home.FullName
            : Path.Combine(context.Home.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, context.Paths.ClaudeDirectory);
        Assert.Equal(Path.Combine(expected, ".claude.json"), context.Paths.ClaudeMcpFile);
        Assert.Equal(expected, context.Paths.Absolute(value));
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("COPILOT_HOME")]
    [InlineData("OPENCODE_CONFIG_DIR")]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("VSCODE_APPDATA")]
    public void Overrides_UseTheSameHomeExpansion(string variable)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable(variable, @"~\custom-config");
        var expected = Path.Combine(context.Home.FullName, "custom-config");

        Assert.Equal(expected, context.Paths.Override(variable));
        if (variable == "COPILOT_HOME")
        {
            Assert.Equal(expected, context.Paths.CopilotDirectory);
        }
    }

    [Fact]
    public void Absolute_DoesNotInterpretAnotherUsersTildeAsTheCurrentHome()
    {
        using var context = new AgentConfigurationTestContext(output);

        Assert.Equal(Path.Combine(context.Project.FullName, "~other-user"), context.Paths.Absolute("~other-user"));
    }

    [Fact]
    public async Task Resolve_DeduplicatesProjectAndUserDirectorySymlinkAliases()
    {
        using var context = new AgentConfigurationTestContext(output);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(context.Project.FullName, ".github", "copilot"));
        var alias = Path.Combine(context.Home.FullName, "copilot-link");
        TestSymlinkHelper.TryCreateSymlink(alias, projectDirectory.FullName);
        context.SetVariable("COPILOT_HOME", alias);

        Assert.Equal(AgentConfigurationPath.Resolve(projectDirectory.FullName), AgentConfigurationPath.Resolve(alias));
        var result = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp])).DefaultTimeout();

        var target = Assert.Single(result);
        Assert.Equal(AgentConfigurationStatus.Configured, target.Status);
        Assert.Equal(AgentConfigurationScope.User, target.Scope);
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
        Assert.True(File.Exists(Path.Combine(projectDirectory.FullName, "settings.json")));
    }

    [Fact]
    public async Task Resolve_PreservesFileSymlinksAndUsesOnePhysicalIdentity()
    {
        using var context = new AgentConfigurationTestContext(output);
        var file = Path.Combine(context.Project.FullName, "settings.json");
        var alias = Path.Combine(context.Project.FullName, "alias.json");
        await File.WriteAllTextAsync(file, "{}").DefaultTimeout();
        TestSymlinkHelper.TryCreateSymlink(alias, file, isDirectory: false);
        var originalLink = new FileInfo(alias).LinkTarget;

        Assert.Equal(AgentConfigurationPath.Resolve(file), AgentConfigurationPath.Resolve(alias));
        Assert.Equal(originalLink, new FileInfo(alias).LinkTarget);
        Assert.Equal("{}", await File.ReadAllTextAsync(file).DefaultTimeout());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_RejectsDanglingFileAndDirectoryLinks(bool directory)
    {
        using var context = new AgentConfigurationTestContext(output);
        var missing = Path.Combine(context.Project.FullName, "missing-target");
        var alias = Path.Combine(context.Project.FullName, "dangling-link");
        TestSymlinkHelper.TryCreateSymlink(alias, missing, isDirectory: directory);

        Assert.Throws<AgentConfigurationException>(() => AgentConfigurationPath.Resolve(alias));
        Assert.False(Path.Exists(missing));
    }

    [Fact]
    public async Task Resolve_DeduplicatesCaseAliasesForMissingSettingsOnCaseInsensitiveMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("This regression covers case-insensitive macOS filesystems.");
        }

        using var context = new AgentConfigurationTestContext(output);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(context.Project.FullName, ".github", "copilot"));
        var caseAlias = Path.Combine(context.Project.FullName, ".GITHUB", "COPILOT");
        if (!Directory.Exists(caseAlias))
        {
            Assert.Skip("The test volume is case-sensitive.");
        }

        context.SetVariable("COPILOT_HOME", caseAlias);
        var projectFile = Path.Combine(projectDirectory.FullName, "settings.json");
        var aliasFile = Path.Combine(caseAlias, "settings.json");
        Assert.False(File.Exists(projectFile));
        Assert.Equal(AgentConfigurationPath.Resolve(projectFile), AgentConfigurationPath.Resolve(aliasFile));

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotCli])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.True(File.Exists(projectFile));
        Assert.Equal(AgentConfigurationPath.Resolve(projectFile), AgentConfigurationPath.Resolve(Path.Combine(caseAlias, "SETTINGS.JSON")));
    }

    [Fact]
    public async Task Resolve_DoesNotCollapseDistinctNamesOnCaseSensitiveVolumes()
    {
        using var context = new AgentConfigurationTestContext(output);
        var original = Path.Combine(context.Project.FullName, "Settings.json");
        var alternate = Path.Combine(context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(original, "{}").DefaultTimeout();
        if (File.Exists(alternate))
        {
            Assert.Skip("The test volume is case-insensitive.");
        }

        await File.WriteAllTextAsync(alternate, """{"different":true}""").DefaultTimeout();

        Assert.NotEqual(AgentConfigurationPath.Resolve(original), AgentConfigurationPath.Resolve(alternate));
    }
}
