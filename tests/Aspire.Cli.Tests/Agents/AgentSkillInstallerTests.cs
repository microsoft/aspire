// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
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
        using var context = new AgentConfigurationTestContext(outputHelper);
        var installer = context.CreateManagedSkillInstaller();
        var request = new AgentInitRequest(
            context.Project,
            new AgentAssetSelection(Mcp: true, playwright, dotnetInspect, AspireSkills: true),
            noClients ? [] : [TestAgentEnvironments.Default.Copilot],
            []);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Equal(0, context.Npm.ResolveCallCount);
        Assert.Equal(0, context.Playwright.GetVersionCallCount);
        Assert.Equal(0, context.Playwright.InstallSkillsCallCount);
    }

    [Theory]
    [InlineData("copilot", ".agents")]
    [InlineData("vscode", ".agents")]
    [InlineData("opencode", ".agents")]
    [InlineData("claude", ".claude")]
    public async Task InstallAsync_DotnetInspectBeforeAppHost_WritesOnlySelectedClientLocations(string clientId, string nativeDirectory)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var project = new DirectoryInfo(Path.Combine(context.Workspace.Path, "project"));
        var installer = context.CreateManagedSkillInstaller(project, context.Home);
        var client = TestAgentEnvironments.Default.All.Single(client => client.Id == clientId);
        var request = CreateRequest(project, [client], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], results.Select(static result => result.Scope));
        Assert.All(results, result =>
        {
            Assert.Equal(AgentAssetKind.DotnetInspect, result.Asset);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal([client], result.Environments);
        });
        Assert.Empty(project.EnumerateFiles());
        Assert.Equal([nativeDirectory], project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([nativeDirectory], context.Home.EnumerateDirectories().Select(static directory => directory.Name));
        foreach (var result in results)
        {
            Assert.Equal(DotnetInspectSkill.Content,
                await File.ReadAllTextAsync(Path.Combine(result.TargetPath, "SKILL.md")));
        }
        Assert.Equal(0, context.Npm.ResolveCallCount);
        Assert.Equal(0, context.Playwright.GetVersionCallCount);
        Assert.Equal(0, context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_AllClients_InstallsAndGeneratesPlaywrightOnceForBothScopes()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode, TestAgentEnvironments.Default.VsCode, TestAgentEnvironments.Default.OpenCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(8, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(1, context.Npm.ResolveCallCount);
        Assert.Equal(1, context.Npm.PackCallCount);
        Assert.Equal(1, context.Npm.InstallGlobalCallCount);
        Assert.Equal(1, context.Playwright.InstallSkillsCallCount);
        Assert.False(Directory.Exists(context.Playwright.InstallSkillsWorkingDirectory));
        Assert.NotEqual(context.Project.FullName, context.Playwright.InstallSkillsWorkingDirectory);
        Assert.NotEqual(context.Home.FullName, context.Playwright.InstallSkillsWorkingDirectory);

        foreach (var result in results.Where(static result => result.Asset is AgentAssetKind.Playwright))
        {
            foreach (var (relativePath, content) in context.Playwright.SkillFiles)
            {
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(result.TargetPath, relativePath)));
            }
        }

        var files = Directory.GetFiles(context.Workspace.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(context.Workspace.Path, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        await Verify(string.Join("\n", files), "txt");
    }

    [Fact]
    public async Task InstallAsync_CopilotAppAndCli_DeduplicatesSharedTargetsAndClientIds()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result => Assert.Equal([TestAgentEnvironments.Default.Copilot], result.Environments));
        Assert.Equal(4, results.Select(static result => result.TargetPath).Distinct(StringComparers.FileSystemPath).Count());
        Assert.Equal([".agents"], context.Project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".agents"], context.Home.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal(1, context.Npm.InstallGlobalCallCount);
        Assert.Equal(1, context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_OverlappingProjectAndHome_DeduplicatesScopes()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var installer = context.CreateManagedSkillInstaller(context.Workspace.WorkspaceRoot, context.Workspace.WorkspaceRoot);
        var request = CreateRequest(context.Workspace.WorkspaceRoot, [TestAgentEnvironments.Default.Copilot]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(AgentConfigurationScope.Project, result.Scope);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal([TestAgentEnvironments.Default.Copilot], result.Environments);
        });
        Assert.Equal(1, context.Npm.InstallGlobalCallCount);
        Assert.Equal(1, context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_CaseInsensitiveRoots_DeduplicatesMissingSkillDirectories()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var project = context.Workspace.CreateDirectory("MixedCase");
        var home = new DirectoryInfo(Path.Combine(context.Workspace.Path, "mixedcase"));
        Assert.SkipWhen(!home.Exists, "The test volume is case-sensitive.");
        var installer = context.CreateManagedSkillInstaller(project, home);
        var request = CreateRequest(project, [TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(AgentConfigurationScope.Project, result.Scope);
    }

    [Fact]
    public async Task InstallAsync_CaseSensitiveRoots_PreservesDistinctTargets()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var project = context.Workspace.CreateDirectory("MixedCase");
        var home = new DirectoryInfo(Path.Combine(context.Workspace.Path, "mixedcase"));
        Assert.SkipWhen(home.Exists, "The test volume is case-insensitive.");
        home.Create();
        var installer = context.CreateManagedSkillInstaller(project, home);
        var request = CreateRequest(project, [TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(2, results.Select(static result => result.TargetPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task InstallAsync_SymlinkedSkillDirectories_DeduplicatesPhysicalTargets()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        foreach (var root in new[] { context.Project, context.Home })
        {
            var sharedSkills = root.CreateSubdirectory(Path.Combine(".agents", "skills"));
            var claude = root.CreateSubdirectory(".claude");
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(claude.FullName, "skills"), sharedSkills.FullName);
        }
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode], result.Environments);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        });
        Assert.Equal(1, context.Npm.InstallGlobalCallCount);
        Assert.Equal(1, context.Playwright.InstallSkillsCallCount);
        Assert.NotNull(new DirectoryInfo(Path.Combine(context.Project.FullName, ".claude", "skills")).LinkTarget);
        Assert.NotNull(new DirectoryInfo(Path.Combine(context.Home.FullName, ".claude", "skills")).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_ClaudeOnly_DoesNotCreateSharedClientDirectories()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.ClaudeCode]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal([".claude"], context.Project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".claude"], context.Home.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal(1, context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_ClaudeConfigOverride_ChangesOnlyUserLocation()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var workingDirectory = context.Workspace.CreateDirectory("working");
        var configDirectory = Path.Combine(context.Workspace.Path, "claude-config");
        context.SetVariable("CLAUDE_CONFIG_DIR", configDirectory);
        var installer = context.CreateManagedSkillInstaller(workingDirectory, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(
            Canonical(Path.Combine(context.Project.FullName, ".claude", "skills", "dotnet-inspect")),
            Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).TargetPath);
        Assert.Equal(
            Canonical(Path.Combine(configDirectory, "skills", "dotnet-inspect")),
            Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).TargetPath);
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
        Assert.Empty(workingDirectory.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("~")]
    [InlineData("~/custom-claude")]
    [InlineData(@"~\custom-claude")]
    public async Task InstallAsync_ClaudeConfigOverride_ResolvesAgainstNativeRoot(string overrideKind)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var workingDirectory = context.Workspace.CreateDirectory("working");
        var value = overrideKind == "relative" ? "custom-claude" : overrideKind;
        var configRoot = overrideKind == "relative" ? workingDirectory.FullName : context.Home.FullName;
        context.SetVariable("CLAUDE_CONFIG_DIR", value);
        var installer = context.CreateManagedSkillInstaller(workingDirectory, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var userTarget = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentConfigurationStatus.Configured, userTarget.Status);
        var directory = overrideKind == "~" ? configRoot : Path.Combine(configRoot, "custom-claude");
        Assert.Equal(Canonical(Path.Combine(directory, "skills", "dotnet-inspect")), userTarget.TargetPath);
        Assert.Equal([".claude"], context.Project.EnumerateDirectories().Select(static directory => directory.Name));
    }

    [Fact]
    public async Task InstallAsync_CommonSkillLocation_DoesNotCreateNativeConfigDirectories()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.SetVariable("COPILOT_HOME", Path.Combine(context.Workspace.Path, "copilot-config"));
        context.SetVariable("OPENCODE_CONFIG_DIR", Path.Combine(context.Workspace.Path, "opencode-config"));
        context.SetVariable("CLAUDE_CONFIG_DIR", Path.Combine(context.Workspace.Path, "claude-config"));
        var installer = context.CreateManagedSkillInstaller(context.Project, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.VsCode, TestAgentEnvironments.Default.OpenCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(["home", "project"], context.Workspace.WorkspaceRoot.EnumerateDirectories().Select(static directory => directory.Name).Order(StringComparer.Ordinal));
        Assert.Equal([".agents"], context.Project.EnumerateDirectories().Select(static directory => directory.Name));
        Assert.Equal([".agents"], context.Home.EnumerateDirectories().Select(static directory => directory.Name));
    }

    [Fact]
    public async Task InstallAsync_PreservesUnselectedAndUnownedSkillFilesAndCaches()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var existingFiles = new Dictionary<string, string>
        {
            [Path.Combine(context.Project.FullName, ".claude", "skills", "playwright-cli", "SKILL.md")] = "user's Claude skill",
            [Path.Combine(context.Home.FullName, ".claude", "skills", "dotnet-inspect", "SKILL.md")] = "user's global Claude skill",
            [Path.Combine(context.Project.FullName, ".github", "skills", "aspire", "SKILL.md")] = "legacy GitHub skill",
            [Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md")] = "legacy Aspire skill",
            [Path.Combine(context.Project.FullName, ".agents", "skills", "playwright-cli", "notes.md")] = "personal notes",
            [Path.Combine(context.Home.FullName, ".aspire", "cache", "aspire-skills", "cache.dat")] = "existing cache"
        };
        foreach (var (path, content) in existingFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
        var timestamps = existingFiles.Keys.ToDictionary(static path => path, File.GetLastWriteTimeUtc);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot]);

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
        using var context = new AgentConfigurationTestContext(outputHelper);
        byte[] bytes = [0, 128, 255, 13, 10];
        var relativePath = Path.Combine("assets", "example.bin");
        context.Playwright.SkillFiles[relativePath] = bytes;
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode], dotnetInspect: false);

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
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.Playwright.InstalledVersion = new SemVersion(0, 1, 7);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode]);
        var first = await installer.InstallAsync(request, CancellationToken.None);
        var files = Directory.GetFiles(context.Workspace.Path, "*", SearchOption.AllDirectories);
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
        Assert.Equal(0, context.Npm.InstallGlobalCallCount);
        Assert.Equal(2, context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_ChangedReference_UpdatesOnlyThatFile()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.Playwright.InstalledVersion = new SemVersion(0, 1, 7);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], dotnetInspect: false);
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
        Assert.Equal(context.Playwright.SkillFiles[Path.Combine("references", "commands.md")], await File.ReadAllBytesAsync(referencePath));
    }

    [Fact]
    public async Task InstallAsync_PlaywrightCopyFailure_ReportsAffectedTargetAndContinues()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var skillDirectory = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "playwright-cli"));
        var blocker = Path.Combine(skillDirectory.FullName, "references");
        await File.WriteAllTextAsync(blocker, "user file");
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot]);

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
        using var context = new AgentConfigurationTestContext(outputHelper);
        var blocker = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect", "SKILL.md"));
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

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
        using var context = new AgentConfigurationTestContext(outputHelper);
        var references = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "playwright-cli", "references"));
        var userFile = Path.Combine(context.Workspace.Path, "user.md");
        await File.WriteAllTextAsync(userFile, "user-owned reference");
        TestSymlinkHelper.TryCreateSymlink(Path.Combine(references.FullName, "commands.md"), userFile, isDirectory: false);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal("user-owned reference", await File.ReadAllTextAsync(userFile));
        Assert.NotNull(new FileInfo(Path.Combine(references.FullName, "commands.md")).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_InvalidClaudeUserRoot_DoesNotPreventProjectInstall()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.SetVariable("CLAUDE_CONFIG_DIR", "invalid\0path");
        var installer = context.CreateManagedSkillInstaller(context.Project, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.ClaudeCode], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentConfigurationStatus.Failed, failure.Status);
        Assert.NotNull(failure.Message);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_UnwritableRoots_ReportFailuresWithoutRemovingExistingFiles()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var projectPath = Path.Combine(context.Workspace.Path, "blocked-project");
        var homePath = Path.Combine(context.Workspace.Path, "blocked-home");
        await File.WriteAllTextAsync(projectPath, "project blocker");
        await File.WriteAllTextAsync(homePath, "home blocker");
        var project = new DirectoryInfo(projectPath);
        var home = new DirectoryInfo(homePath);
        var installer = context.CreateManagedSkillInstaller(project, home);
        var request = CreateRequest(project, [TestAgentEnvironments.Default.Copilot]);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, static result =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
            Assert.Equal([TestAgentEnvironments.Default.Copilot], result.Environments);
            Assert.NotNull(result.Message);
        });
        Assert.Equal("project blocker", await File.ReadAllTextAsync(projectPath));
        Assert.Equal("home blocker", await File.ReadAllTextAsync(homePath));
    }

    [Theory]
    [InlineData("provenance", true)]
    [InlineData("missing-skill", false)]
    [InlineData("npm", true)]
    public async Task InstallAsync_PlaywrightFailureDoesNotBlockOtherSelectedAssets(string failure, bool dotnetInspect)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        switch (failure)
        {
            case "provenance":
                context.Provenance.ProvenanceOutcome = ProvenanceVerificationOutcome.PackageDigestMismatch;
                break;
            case "missing-skill":
                context.Playwright.SkillFiles.Clear();
                break;
            case "npm":
                context.Npm.IsAvailable = false;
                break;
        }
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([context.Copilot, context.ClaudeCode], dotnetInspect: dotnetInspect);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(dotnetInspect ? 8 : 4, results.Count);
        Assert.All(results.Where(result => result.Asset is AgentAssetKind.Playwright), result =>
        {
            Assert.Equal(failure == "npm" ? AgentConfigurationStatus.Blocked : AgentConfigurationStatus.Failed, result.Status);
            Assert.NotNull(result.Message);
            Assert.False(Directory.Exists(result.TargetPath));
            if (failure is "npm" or "missing-skill")
            {
                Assert.Equal(failure == "npm" ? AgentCommandStrings.InitCommand_PlaywrightCliSkipped :
                    AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles, result.Message);
            }
        });
        Assert.All(results.Where(result => result.Asset is AgentAssetKind.DotnetInspect),
            result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(failure == "missing-skill" ? 1 : 0, context.Npm.InstallGlobalCallCount);
        Assert.Equal(failure == "missing-skill" ? 1 : 0, context.Playwright.InstallSkillsCallCount);
        Assert.False(Directory.Exists(context.Playwright.InstallSkillsWorkingDirectory));
        if (failure == "npm")
        {
            Assert.Equal(0, context.Npm.ResolveCallCount);
            Assert.Equal(0, context.Playwright.GetVersionCallCount);
        }
        if (!dotnetInspect)
        {
            Assert.Empty(context.Project.EnumerateFileSystemInfos());
            Assert.Empty(context.Home.EnumerateFileSystemInfos());
        }
    }

    [Fact]
    public async Task InstallAsync_Cancellation_DoesNotWriteOrProbe()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var installer = context.CreateManagedSkillInstaller(context.Project, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.Copilot]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(request, cancellation.Token));

        Assert.Equal(0, context.Npm.ResolveCallCount);
        Assert.Equal(0, context.Playwright.GetVersionCallCount);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_RepointedSharedAlias_FailsTargetWithoutUpdatingEitherDirectory()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var shared = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills"));
        var redirected = context.Workspace.CreateDirectory("redirected");
        var claude = context.Project.CreateSubdirectory(".claude");
        var link = Path.Combine(claude.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, shared.FullName);
        foreach (var directory in new[] { shared, redirected })
        {
            var skill = directory.CreateSubdirectory("playwright-cli");
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "existing user skill");
        }
        context.Playwright.OnInstallSkills = _ =>
        {
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, redirected.FullName);
        };
        var installer = context.CreateManagedSkillInstaller(context.Project, context.Home);
        var request = CreateRequest(context.Project, [TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode], dotnetInspect: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var failure = Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project);
        Assert.Equal(AgentConfigurationStatus.Failed, failure.Status);
        Assert.Equal([TestAgentEnvironments.Default.Copilot, TestAgentEnvironments.Default.ClaudeCode], failure.Environments);
        Assert.Contains(AgentCommandStrings.Configuration_ConcurrentChange, failure.Message!);
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
        using var context = new AgentConfigurationTestContext(outputHelper);
        var physicalSkill = context.Workspace.CreateDirectory("shared-skill");
        foreach (var root in new[] { context.Project, context.Home })
        {
            var skills = root.CreateSubdirectory(Path.Combine(".agents", "skills"));
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(skills.FullName, "dotnet-inspect"), physicalSkill.FullName);
        }
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(Canonical(physicalSkill.FullName), result.TargetPath);
        Assert.Equal(DotnetInspectSkill.Content, await File.ReadAllTextAsync(Path.Combine(physicalSkill.FullName, "SKILL.md")));
        Assert.All(new[] { context.Project, context.Home }, static root =>
            Assert.NotNull(new DirectoryInfo(Path.Combine(root.FullName, ".agents", "skills", "dotnet-inspect")).LinkTarget));
    }

    [Fact]
    public async Task InstallAsync_DanglingSkillDirectory_DoesNotInitializeLinkTarget()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var shared = context.Project.CreateSubdirectory(".agents");
        var missing = Path.Combine(context.Workspace.Path, "missing");
        var link = Path.Combine(shared.FullName, "skills");
        TestSymlinkHelper.TryCreateSymlink(link, missing);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results, static result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.False(Directory.Exists(missing));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public async Task InstallAsync_LinkedSkillFileInsideTarget_ReplacesPhysicalFileAndPreservesLink()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var skill = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var physicalPath = Path.Combine(skill.FullName, "content.md");
        await File.WriteAllTextAsync(physicalPath, "old skill");
        var link = Path.Combine(skill.FullName, "SKILL.md");
        TestSymlinkHelper.TryCreateSymlink(link, physicalPath, isDirectory: false);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(DotnetInspectSkill.Content, await File.ReadAllTextAsync(physicalPath));
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

        using var context = new AgentConfigurationTestContext(outputHelper);
        var skill = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var path = Path.Combine(skill.FullName, "SKILL.md");
        await File.WriteAllTextAsync(path, "working skill");
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

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

        using var context = new AgentConfigurationTestContext(outputHelper);
        var skill = context.Project.CreateSubdirectory(Path.Combine(".agents", "skills", "dotnet-inspect"));
        var path = Path.Combine(skill.FullName, "SKILL.md");
        await File.WriteAllTextAsync(path, "working skill");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, mode);
        var installer = context.CreateManagedSkillInstaller();
        var request = context.ManagedRequest([TestAgentEnvironments.Default.Copilot], playwright: false);

        var results = await installer.InstallAsync(request, CancellationToken.None);

        Assert.All(results, static result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(DotnetInspectSkill.Content, await File.ReadAllTextAsync(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(["SKILL.md"], skill.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    private static AgentInitRequest CreateRequest(
        DirectoryInfo project,
        IReadOnlyList<IAgentEnvironmentScanner> clients,
        bool playwright = true,
        bool dotnetInspect = true) =>
        new(project, new AgentAssetSelection(Mcp: false, playwright, dotnetInspect, AspireSkills: false), clients, []);

    private static string Canonical(string path) => AgentPath.Resolve(path);
}
