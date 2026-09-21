// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class AgentSkillInstallerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task InstallAsync_WhenNoManagedWorkIsSelected_DoesNotWriteOrProbe(bool playwright, bool dotnetInspect, bool noClients)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = new AgentInitRequest(
            project,
            new AgentAssetSelection(Mcp: true, playwright, dotnetInspect, AspireSkills: true),
            noClients ? [] : [AgentClientKind.CopilotCli],
            []);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(project.EnumerateFileSystemInfos());
        Assert.Empty(home.EnumerateFileSystemInfos());
        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Theory]
    [InlineData((int)AgentClientKind.CopilotCli, ".agents")]
    [InlineData((int)AgentClientKind.CopilotApp, ".agents")]
    [InlineData((int)AgentClientKind.VsCode, ".agents")]
    [InlineData((int)AgentClientKind.OpenCode, ".agents")]
    [InlineData((int)AgentClientKind.ClaudeCode, ".claude")]
    public async Task InstallAsync_DotnetInspectBeforeAppHost_WritesOnlySelectedClientLocations(int clientKind, string nativeDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = new DirectoryInfo(Path.Combine(workspace.Path, "project"));
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var client = (AgentClientKind)clientKind;
        var request = CreateRequest(project, [client], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], results.Select(static result => result.Scope));
        Assert.All(results, result =>
        {
            Assert.Equal(AgentAssetKind.DotnetInspect, result.Asset);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal([client], result.Clients);
        });
        Assert.Empty(project.EnumerateFiles());
        Assert.Equal([nativeDirectory], project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([nativeDirectory], home.EnumerateDirectories().Select(static directory => directory.Name));
        foreach (var result in results)
        {
            Assert.Equal(CommonAgentApplicators.DotnetInspectSkillFileContent,
                await File.ReadAllTextAsync(Path.Combine(result.TargetPath, "SKILL.md")));
        }
        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_AllClients_InstallsAndGeneratesPlaywrightOnceForBothScopes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, Enum.GetValues<AgentClientKind>());

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(8, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(1, npmRunner.ResolveCallCount);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
        Assert.NotEqual(project.FullName, playwrightRunner.InstallSkillsWorkingDirectory);
        Assert.NotEqual(home.FullName, playwrightRunner.InstallSkillsWorkingDirectory);

        foreach (var result in results.Where(static result => result.Asset is AgentAssetKind.Playwright))
        {
            foreach (var (relativePath, content) in playwrightRunner.SkillFiles)
            {
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(result.TargetPath, relativePath)));
            }
        }

        var files = Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspace.Path, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        await Verify(string.Join("\n", files), "txt");
    }

    [Fact]
    public async Task InstallAsync_CopilotAppAndCli_DeduplicatesSharedTargetsAndClientIds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.CopilotCli]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result => Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp], result.Clients));
        Assert.Equal(4, results.Select(static result => result.TargetPath).Distinct(StringComparers.FileSystemPath).Count());
        Assert.Equal([".agents"], project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".agents"], home.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_OverlappingProjectAndHome_DeduplicatesScopes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(workspace.WorkspaceRoot, workspace.WorkspaceRoot, npmRunner, playwrightRunner);
        var request = CreateRequest(workspace.WorkspaceRoot, [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(AgentConfigurationScope.Project, result.Scope);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp], result.Clients);
        });
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_CaseInsensitiveRoots_DeduplicatesMissingSkillDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("MixedCase");
        var home = new DirectoryInfo(Path.Combine(workspace.Path, "mixedcase"));
        Assert.SkipWhen(!home.Exists, "The test volume is case-sensitive.");
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(AgentConfigurationScope.Project, result.Scope);
    }

    [Fact]
    public async Task InstallAsync_CaseSensitiveRoots_PreservesDistinctTargets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("MixedCase");
        var home = new DirectoryInfo(Path.Combine(workspace.Path, "mixedcase"));
        Assert.SkipWhen(home.Exists, "The test volume is case-insensitive.");
        home.Create();
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(2, results.Select(static result => result.TargetPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task InstallAsync_SymlinkedSkillDirectories_DeduplicatesPhysicalTargets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        foreach (var root in new[] { project, home })
        {
            var sharedSkills = root.CreateSubdirectory(Path.Combine(".agents", "skills"));
            var claude = root.CreateSubdirectory(".claude");
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(claude.FullName, "skills"), sharedSkills.FullName);
        }
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], result.Clients);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        });
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.NotNull(new DirectoryInfo(Path.Combine(project.FullName, ".claude", "skills")).LinkTarget);
        Assert.NotNull(new DirectoryInfo(Path.Combine(home.FullName, ".claude", "skills")).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_ClaudeOnly_DoesNotCreateSharedClientDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, CreateNpmRunner(), playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.ClaudeCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal([".claude"], project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".claude"], home.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_ClaudeConfigOverride_ChangesOnlyUserLocation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var workingDirectory = workspace.CreateDirectory("working");
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var configDirectory = Path.Combine(workspace.Path, "claude-config");
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = configDirectory });
        var installer = CreateInstaller(workingDirectory, home, CreateNpmRunner(), new FakePlaywrightCliRunner(), environment);
        var request = CreateRequest(project, [AgentClientKind.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(
            Canonical(Path.Combine(project.FullName, ".claude", "skills", "dotnet-inspect")),
            Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).TargetPath);
        Assert.Equal(
            Canonical(Path.Combine(configDirectory, "skills", "dotnet-inspect")),
            Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).TargetPath);
        Assert.Empty(home.EnumerateFileSystemInfos());
        Assert.Empty(workingDirectory.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("~")]
    [InlineData("~/custom-claude")]
    [InlineData(@"~\custom-claude")]
    public async Task InstallAsync_ClaudeConfigOverride_ResolvesAgainstNativeRoot(string overrideKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var workingDirectory = workspace.CreateDirectory("working");
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var value = overrideKind == "relative" ? "custom-claude" : overrideKind;
        var configRoot = overrideKind == "relative" ? workingDirectory.FullName : home.FullName;
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = value });
        var installer = CreateInstaller(workingDirectory, home, CreateNpmRunner(), new FakePlaywrightCliRunner(), environment);
        var request = CreateRequest(project, [AgentClientKind.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var userTarget = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentConfigurationStatus.Configured, userTarget.Status);
        var directory = overrideKind == "~" ? configRoot : Path.Combine(configRoot, "custom-claude");
        Assert.Equal(Canonical(Path.Combine(directory, "skills", "dotnet-inspect")), userTarget.TargetPath);
        Assert.Equal([".claude"], project.EnumerateDirectories().Select(static directory => directory.Name));
    }

    [Fact]
    public async Task InstallAsync_CommonSkillLocation_DoesNotCreateNativeConfigDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = Path.Combine(workspace.Path, "copilot-config"),
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(workspace.Path, "opencode-config"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(workspace.Path, "claude-config")
        });
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner(), environment);
        var request = CreateRequest(project, [AgentClientKind.CopilotApp, AgentClientKind.VsCode, AgentClientKind.OpenCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(["home", "project"], workspace.WorkspaceRoot.EnumerateDirectories().Select(static directory => directory.Name).Order(StringComparer.Ordinal));
        Assert.Equal([".agents"], project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".agents"], home.EnumerateDirectories().Select(static directory => directory.Name));
    }

    [Fact]
    public async Task InstallAsync_PreservesUnselectedAndUnownedSkillFilesAndCaches()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var existingFiles = new Dictionary<string, string>
        {
            [Path.Combine(project.FullName, ".claude", "skills", "playwright-cli", "SKILL.md")] = "user's Claude skill",
            [Path.Combine(home.FullName, ".claude", "skills", "dotnet-inspect", "SKILL.md")] = "user's global Claude skill",
            [Path.Combine(project.FullName, ".github", "skills", "aspire", "SKILL.md")] = "legacy GitHub skill",
            [Path.Combine(project.FullName, ".agents", "skills", "aspire", "SKILL.md")] = "legacy Aspire skill",
            [Path.Combine(project.FullName, ".agents", "skills", "playwright-cli", "notes.md")] = "personal notes",
            [Path.Combine(home.FullName, ".aspire", "cache", "aspire-skills", "cache.dat")] = "existing cache"
        };
        foreach (var (path, content) in existingFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
        var timestamps = existingFiles.Keys.ToDictionary(static path => path, File.GetLastWriteTimeUtc);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        foreach (var (path, content) in existingFiles)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
            Assert.Equal(timestamps[path], File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task InstallAsync_PlaywrightSupportingFiles_IncludeBinaryContentAtEveryTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var playwrightRunner = new FakePlaywrightCliRunner();
        byte[] bytes = [0, 128, 255, 13, 10];
        var relativePath = Path.Combine("assets", "example.bin");
        playwrightRunner.SkillFiles[relativePath] = bytes;
        var installer = CreateInstaller(project, home, CreateNpmRunner(), playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        foreach (var result in results)
        {
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(result.TargetPath, relativePath)));
        }
    }

    [Fact]
    public async Task InstallAsync_UnchangedContent_PreservesTimestamps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var playwrightRunner = new FakePlaywrightCliRunner { InstalledVersion = new SemVersion(0, 1, 7) };
        var npmRunner = CreateNpmRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode]);
        var first = await installer.InstallAsync(request, CancellationToken.None);
        var files = Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories);
        foreach (var path in files)
        {
            File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        var timestamps = files.ToDictionary(static path => path, File.GetLastWriteTimeUtc);

        var second = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(first.Count, second.Count);
        Assert.All(second, static result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        foreach (var path in files)
        {
            Assert.Equal(timestamps[path], File.GetLastWriteTimeUtc(path));
        }
        Assert.Equal(0, npmRunner.InstallGlobalCallCount);
        Assert.Equal(2, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_ChangedReference_UpdatesOnlyThatFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var playwrightRunner = new FakePlaywrightCliRunner { InstalledVersion = new SemVersion(0, 1, 7) };
        var installer = CreateInstaller(project, home, CreateNpmRunner(), playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], dotnetInspect: false);
        var first = await installer.InstallAsync(request, CancellationToken.None);
        var projectTarget = Assert.Single(first, static result => result.Scope is AgentConfigurationScope.Project);
        var skillPath = Path.Combine(projectTarget.TargetPath, "SKILL.md");
        var referencePath = Path.Combine(projectTarget.TargetPath, "references", "commands.md");
        var skillTimestamp = File.GetLastWriteTimeUtc(skillPath);
        await File.WriteAllTextAsync(referencePath, "old reference");

        var second = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(second, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(second, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(skillTimestamp, File.GetLastWriteTimeUtc(skillPath));
        Assert.Equal(playwrightRunner.SkillFiles[Path.Combine("references", "commands.md")], await File.ReadAllBytesAsync(referencePath));
    }

    [Fact]
    public async Task InstallAsync_PlaywrightCopyFailure_ReportsAffectedTargetAndContinues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var skillDirectory = project.CreateSubdirectory(Path.Combine(".agents", "skills", "playwright-cli"));
        var blocker = Path.Combine(skillDirectory.FullName, "references");
        await File.WriteAllTextAsync(blocker, "user file");
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Status is AgentConfigurationStatus.Failed);
        Assert.Equal(AgentAssetKind.Playwright, failure.Asset);
        Assert.Equal(AgentConfigurationScope.Project, failure.Scope);
        Assert.NotNull(failure.Message);
        Assert.Contains(failure.TargetPath, failure.Message);
        Assert.All(results.Where(result => result != failure), static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal("user file", await File.ReadAllTextAsync(blocker));
        Assert.False(File.Exists(Path.Combine(skillDirectory.FullName, "SKILL.md")));
    }

    [Fact]
    public async Task InstallAsync_DotnetInspectCopyFailure_ReportsAffectedTargetAndContinues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var blocker = project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect", "SKILL.md"));
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Status is AgentConfigurationStatus.Failed);
        Assert.Equal(AgentConfigurationScope.Project, failure.Scope);
        Assert.NotNull(failure.Message);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.True(Directory.Exists(blocker.FullName));
    }

    [Fact]
    public async Task InstallAsync_LinkedSupportingFileOutsideTarget_PreservesUserFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var references = project.CreateSubdirectory(Path.Combine(".agents", "skills", "playwright-cli", "references"));
        var userFile = Path.Combine(workspace.Path, "user.md");
        await File.WriteAllTextAsync(userFile, "user-owned reference");
        TestSymlinkHelper.TryCreateSymlink(Path.Combine(references.FullName, "commands.md"), userFile, isDirectory: false);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal("user-owned reference", await File.ReadAllTextAsync(userFile));
        Assert.NotNull(new FileInfo(Path.Combine(references.FullName, "commands.md")).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_InvalidClaudeUserRoot_DoesNotPreventProjectInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "invalid\0path" });
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner(), environment);
        var request = CreateRequest(project, [AgentClientKind.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentConfigurationStatus.Failed, failure.Status);
        Assert.NotNull(failure.Message);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Empty(home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_UnwritableRoots_ReportFailuresWithoutRemovingExistingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.Path, "project");
        var homePath = Path.Combine(workspace.Path, "home");
        await File.WriteAllTextAsync(projectPath, "project blocker");
        await File.WriteAllTextAsync(homePath, "home blocker");
        var project = new DirectoryInfo(projectPath);
        var home = new DirectoryInfo(homePath);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
            Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp], result.Clients);
            Assert.NotNull(result.Message);
        });
        Assert.Equal("project blocker", await File.ReadAllTextAsync(projectPath));
        Assert.Equal("home blocker", await File.ReadAllTextAsync(homePath));
    }

    [Fact]
    public async Task InstallAsync_PlaywrightVerificationFailure_FailsAffectedTargetsButInstallsDotnetInspect()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var provenanceChecker = new FakeNpmProvenanceChecker { ProvenanceOutcome = ProvenanceVerificationOutcome.PackageDigestMismatch };
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner, provenanceChecker: provenanceChecker);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(8, results.Count);
        Assert.All(results.Where(static result => result.Asset is AgentAssetKind.Playwright), static result =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
            Assert.NotNull(result.Message);
            Assert.False(Directory.Exists(result.TargetPath));
        });
        Assert.All(results.Where(static result => result.Asset is AgentAssetKind.DotnetInspect),
            static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(0, npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_PlaywrightMissingGeneratedOutput_FailsAllSelectedTargets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var playwrightRunner = new FakePlaywrightCliRunner();
        playwrightRunner.SkillFiles.Clear();
        var installer = CreateInstaller(project, home, CreateNpmRunner(), playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
            Assert.Equal(AgentSkillInstallerStrings.PlaywrightMissingSkill, result.Message);
        });
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.Empty(project.EnumerateFileSystemInfos());
        Assert.Empty(home.EnumerateFileSystemInfos());
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_NpmUnavailable_BlocksPlaywrightWithoutBlockingDotnetInspect()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = new FakeNpmRunner { IsAvailable = false };
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results.Where(static result => result.Asset is AgentAssetKind.Playwright), static result =>
        {
            Assert.Equal(AgentConfigurationStatus.Blocked, result.Status);
            Assert.Equal(AgentSkillInstallerStrings.PlaywrightNpmRequired, result.Message);
        });
        Assert.All(results.Where(static result => result.Asset is AgentAssetKind.DotnetInspect),
            static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
    }

    [Fact]
    public async Task InstallAsync_Cancellation_DoesNotWriteOrProbe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(project, home, npmRunner, playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(request, cancellation.Token));

        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
        Assert.Empty(project.EnumerateFileSystemInfos());
        Assert.Empty(home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_RepointedSharedAlias_FailsTargetWithoutUpdatingEitherDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var shared = project.CreateSubdirectory(Path.Combine(".agents", "skills"));
        var redirected = workspace.CreateDirectory("redirected");
        var claude = project.CreateSubdirectory(".claude");
        var link = Path.Combine(claude.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, shared.FullName);
        foreach (var directory in new[] { shared, redirected })
        {
            var skill = directory.CreateSubdirectory("playwright-cli");
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "existing user skill");
        }
        var playwrightRunner = new FakePlaywrightCliRunner
        {
            OnInstallSkills = _ =>
            {
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, redirected.FullName);
            }
        };
        var installer = CreateInstaller(project, home, CreateNpmRunner(), playwrightRunner);
        var request = CreateRequest(project, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project);
        Assert.Equal(AgentConfigurationStatus.Failed, failure.Status);
        Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], failure.Clients);
        Assert.Contains(AgentConfigurationStrings.ConcurrentChange, failure.Message!);
        Assert.All(results.Where(static result => result.Scope is AgentConfigurationScope.User),
            static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        foreach (var directory in new[] { shared, redirected })
        {
            var skill = Path.Combine(directory.FullName, "playwright-cli");
            Assert.Equal("existing user skill", await File.ReadAllTextAsync(Path.Combine(skill, "SKILL.md")));
            Assert.Equal(["SKILL.md"], Directory.EnumerateFileSystemEntries(skill).Select(Path.GetFileName));
        }
    }

    [Fact]
    public async Task InstallAsync_LinkedSkillDirectories_DeduplicatesLeafDirectoryLinks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var physicalSkill = workspace.CreateDirectory("shared-skill");
        foreach (var root in new[] { project, home })
        {
            var skills = root.CreateSubdirectory(Path.Combine(".agents", "skills"));
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(skills.FullName, "dotnet-inspect"), physicalSkill.FullName);
        }
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(Canonical(physicalSkill.FullName), result.TargetPath);
        Assert.Equal(CommonAgentApplicators.DotnetInspectSkillFileContent, await File.ReadAllTextAsync(Path.Combine(physicalSkill.FullName, "SKILL.md")));
        Assert.All(new[] { project, home }, static root =>
            Assert.NotNull(new DirectoryInfo(Path.Combine(root.FullName, ".agents", "skills", "dotnet-inspect")).LinkTarget));
    }

    [Fact]
    public async Task InstallAsync_DanglingSkillDirectory_DoesNotInitializeLinkTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var shared = project.CreateSubdirectory(".agents");
        var missing = Path.Combine(workspace.Path, "missing");
        var link = Path.Combine(shared.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, missing);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.False(Directory.Exists(missing));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_LinkedSkillFileInsideTarget_ReplacesPhysicalFileAndPreservesLink()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var skill = project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var physicalPath = Path.Combine(skill.FullName, "content.md");
        await File.WriteAllTextAsync(physicalPath, "old skill");
        var link = Path.Combine(skill.FullName, "SKILL.md");
        TestSymlinkHelper.TryCreateSymlink(link, physicalPath, isDirectory: false);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(CommonAgentApplicators.DotnetInspectSkillFileContent, await File.ReadAllTextAsync(physicalPath));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(["SKILL.md", "content.md"], Directory.EnumerateFileSystemEntries(skill.FullName).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_ReplacementFailure_PreservesExistingSkillAndCleansStaging()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows sharing modes provide a deterministic replacement failure.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var skill = project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var path = Path.Combine(skill.FullName, "SKILL.md");
        await File.WriteAllTextAsync(path, "working skill");
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        // Permit the optimistic byte reads but not replacement/deletion of the destination.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var results = await installer.InstallAsync(request, CancellationToken.None);

            var failure = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project);
            Assert.Equal(AgentConfigurationStatus.Failed, failure.Status);
            Assert.Contains(failure.TargetPath, failure.Message!);
            Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        }

        Assert.Equal("working skill", await File.ReadAllTextAsync(path));
        Assert.Equal(["SKILL.md"], skill.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task InstallAsync_Replacement_PreservesExistingUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permission preservation is not applicable on Windows.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var skill = project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var path = Path.Combine(skill.FullName, "SKILL.md");
        await File.WriteAllTextAsync(path, "working skill");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, mode);
        var installer = CreateInstaller(project, home, CreateNpmRunner(), new FakePlaywrightCliRunner());
        var request = CreateRequest(project, [AgentClientKind.CopilotCli], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(CommonAgentApplicators.DotnetInspectSkillFileContent, await File.ReadAllTextAsync(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(["SKILL.md"], skill.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    private static AgentInitRequest CreateRequest(
        DirectoryInfo project,
        IReadOnlyList<AgentClientKind> clients,
        bool playwright = true,
        bool dotnetInspect = true) =>
        new(project, new AgentAssetSelection(Mcp: false, playwright, dotnetInspect, AspireSkills: false), clients, []);

    private static FakeNpmRunner CreateNpmRunner() => new()
    {
        ResolveResult = new NpmPackageInfo { Version = new SemVersion(0, 1, 7) }
    };

    private static AgentSkillInstaller CreateInstaller(
        DirectoryInfo workingDirectory,
        DirectoryInfo home,
        FakeNpmRunner npmRunner,
        FakePlaywrightCliRunner playwrightRunner,
        IEnvironment? environment = null,
        FakeNpmProvenanceChecker? provenanceChecker = null)
    {
        var context = new CliExecutionContext(
            workingDirectory,
            new DirectoryInfo(Path.Combine(home.FullName, "hives")),
            new DirectoryInfo(Path.Combine(home.FullName, "cache")),
            new DirectoryInfo(Path.Combine(home.FullName, "sdks")),
            new DirectoryInfo(Path.Combine(home.FullName, "logs")),
            Path.Combine(home.FullName, "logs", "test.log"),
            "test",
            homeDirectory: home);
        var playwrightInstaller = new PlaywrightCliInstaller(
            npmRunner,
            provenanceChecker ?? new FakeNpmProvenanceChecker(),
            playwrightRunner,
            new TestInteractionService(),
            new ConfigurationBuilder().Build(),
            NullLogger<PlaywrightCliInstaller>.Instance);

        return new AgentSkillInstaller(
            playwrightInstaller,
            context,
            new AgentConfigurationPaths(context, environment ?? new TestEnvironment()),
            NullLogger<AgentSkillInstaller>.Instance);
    }

    private static string Canonical(string path) => AgentConfigurationPath.Resolve(path);
}
