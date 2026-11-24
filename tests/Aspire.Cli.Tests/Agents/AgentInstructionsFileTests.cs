// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Tests.Agents;

public class AgentInstructionsFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WriteAsync_CreatesFileWithStartFlowAndSkillsPluginNote()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "My App.AppHost", "My App.AppHost.csproj");

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, aspireSkillsPluginRegistered: true, CancellationToken.None);

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

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Appended, change);
        Assert.Equal(
            existing + "\r\n\r\n" + AgentInstructionsFile.CreateSection("apphost.cs", false, "\r\n") + "\r\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_ReplacesOnlyTheAspireSection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var oldSection = AgentInstructionsFile.CreateSection("old.cs", false, "\n");
        await File.WriteAllTextAsync(GetFilePath(workspace), $"# Rules\n\n{oldSection}\n\n## More rules\nKeep tests green.\n");

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, aspireSkillsPluginRegistered: true, CancellationToken.None);
        var unchanged = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, aspireSkillsPluginRegistered: true, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Updated, change);
        Assert.Equal(AgentInstructionsFileChange.Unchanged, unchanged);
        Assert.Equal(
            $"# Rules\n\n{AgentInstructionsFile.CreateSection("apphost.cs", aspireSkillsPluginRegistered: true, "\n")}\n\n## More rules\nKeep tests green.\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_AppendsThroughSymbolicLinkWithoutLeavingStagingFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var target = Path.Combine(workspace.WorkspaceRoot.FullName, "CLAUDE.md");
        await File.WriteAllTextAsync(target, "# Team rules\n");
        TestSymlinkHelper.TryCreateSymlink(GetFilePath(workspace), target, isDirectory: false);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Appended, change);
        Assert.Equal(target, new FileInfo(GetFilePath(workspace)).LinkTarget);
        Assert.Equal($"# Team rules\n\n{AgentInstructionsFile.CreateSection("apphost.cs", false, "\n")}\n", await File.ReadAllTextAsync(target));
        Assert.Equal(
            ["AGENTS.md", "CLAUDE.md"],
            workspace.WorkspaceRoot.EnumerateFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_RelatesDiscoveredAppHostToDirectoryGivenThroughSymbolicLink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var real = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "real"));
        var link = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        TestSymlinkHelper.TryCreateSymlink(link, real.FullName);
        // Discovery returns the AppHost with links resolved, while the workspace root is given through the link.
        var appHost = PathNormalizer.ResolveSymlinks(Path.Combine(real.FullName, "App", "apphost.cs"));

        var change = await AgentInstructionsFile.WriteAsync(new DirectoryInfo(link), appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Created, change);
        var newLine = Environment.NewLine;
        Assert.Equal(
            $"# Agent instructions{newLine}{newLine}{AgentInstructionsFile.CreateSection("App/apphost.cs", false, newLine)}{newLine}",
            await File.ReadAllTextAsync(Path.Combine(link, AgentInstructionsFile.FileName)));
    }

    [Fact]
    public async Task WriteAsync_KeepsLinkedSubfolderNameInAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var real = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "elsewhere", "App"));
        var link = Path.Combine(workspace.WorkspaceRoot.FullName, "app");
        TestSymlinkHelper.TryCreateSymlink(link, real.FullName);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, Path.Combine(link, "apphost.cs"), false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Created, change);
        var newLine = Environment.NewLine;
        Assert.Equal(
            $"# Agent instructions{newLine}{newLine}{AgentInstructionsFile.CreateSection("app/apphost.cs", false, newLine)}{newLine}",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_CreatesTargetOfDanglingSymbolicLink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var target = Path.Combine(workspace.WorkspaceRoot.FullName, "CLAUDE.md");
        TestSymlinkHelper.TryCreateSymlink(GetFilePath(workspace), "CLAUDE.md", isDirectory: false);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Created, change);
        Assert.Equal("CLAUDE.md", new FileInfo(GetFilePath(workspace)).LinkTarget);
        var newLine = Environment.NewLine;
        Assert.Equal(
            $"# Agent instructions{newLine}{newLine}{AgentInstructionsFile.CreateSection("apphost.cs", false, newLine)}{newLine}",
            await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData("Our generated block starts with `<!-- aspire:begin` and is refreshed by the CLI.\n")]
    [InlineData("<!-- aspire:begin -->\nAn unterminated section.\n")]
    public async Task WriteAsync_LeavesFileWithAnotherBeginMarkerBeforeTheEndMarkerUnchanged(string earlierText)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{earlierText}\nKeep this team rule.\n\n{AgentInstructionsFile.CreateSection("old.cs", false, "\n")}\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.SkippedMalformedSection, change);
        Assert.Equal(existing, await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_LeavesFileWithUnterminatedSectionUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{AgentInstructionsFile.SectionStartMarkerPrefix} -->\nMy own notes.\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.SkippedMalformedSection, change);
        Assert.Equal(existing, await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_PreservesTextAfterEndMarkerOnSameLine()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var oldSection = AgentInstructionsFile.CreateSection("old.cs", false, "\n");
        await File.WriteAllTextAsync(GetFilePath(workspace), $"# Rules\n\n{oldSection} Keep this team rule.\nMore rules.\n");

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.Updated, change);
        Assert.Equal(
            $"# Rules\n\n{AgentInstructionsFile.CreateSection("apphost.cs", false, "\n")} Keep this team rule.\nMore rules.\n",
            await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_LeavesFileWithUnclosedEndMarkerUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{AgentInstructionsFile.SectionStartMarkerPrefix} -->\nOld section.\n{AgentInstructionsFile.SectionEndMarkerPrefix}\nMy own notes.\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

        Assert.Equal(AgentInstructionsFileChange.SkippedMalformedSection, change);
        Assert.Equal(existing, await ReadFileAsync(workspace));
    }

    [Fact]
    public async Task WriteAsync_LeavesFileWithUnclosedEndMarkerBeforeAnotherCommentUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var existing = $"# Rules\n\n{AgentInstructionsFile.SectionStartMarkerPrefix} -->\nOld section.\n{AgentInstructionsFile.SectionEndMarkerPrefix}\nKeep this team rule.\n<!-- unrelated note -->\n";
        await File.WriteAllTextAsync(GetFilePath(workspace), existing);

        var change = await AgentInstructionsFile.WriteAsync(workspace.WorkspaceRoot, appHost, false, CancellationToken.None);

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
        var section = AgentInstructionsFile.CreateSection(appHostPath, false, "\n");

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
