// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Projects;

public class RepositoryToolUpdaterTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindManifestsAsync_FindsDotNetManifestInAncestor(bool useConfigDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var workingDirectory = root.CreateSubdirectory(Path.Combine("src", "app"));
        var manifestDirectory = useConfigDirectory ? root.CreateSubdirectory(".config") : root;
        var manifestPath = await WriteManifestAsync(manifestDirectory, isNpm: false, "13.3.0");
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var manifests = await updater.FindManifestsAsync(workingDirectory, CancellationToken.None);

        var manifest = Assert.Single(manifests);
        Assert.Equal(manifestPath, manifest.File.FullName);
        Assert.False(manifest.IsNpm);
        Assert.Equal("Aspire.Cli", manifest.PackageId);
        Assert.Equal("13.3.0", Assert.Single(manifest.References).Version);
    }

    [Fact]
    public async Task FindManifestsAsync_PrefersConfigDotNetManifestOverBareManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var configPath = await WriteManifestAsync(root.CreateSubdirectory(".config"), isNpm: false, "13.3.0");
        await WriteManifestAsync(root, isNpm: false, "13.2.0");
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        Assert.Equal(configPath, Assert.Single(manifests).File.FullName);
    }

    [Fact]
    public async Task FindManifestsAsync_SkipsUnrelatedNonRootDotNetManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var child = root.CreateSubdirectory("src");
        var unrelatedPath = Path.Combine(child.CreateSubdirectory(".config").FullName, "dotnet-tools.json");
        const string unrelatedContent = """{"version":1,"isRoot":false,"tools":{"dotnet-ef":{"version":"10.0.0","commands":["dotnet-ef"]}}}""";
        await File.WriteAllTextAsync(unrelatedPath, unrelatedContent);
        var manifestPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var manifests = await updater.FindManifestsAsync(child, CancellationToken.None);

        Assert.Equal(manifestPath, Assert.Single(manifests).File.FullName);
        Assert.Equal(unrelatedContent, await File.ReadAllTextAsync(unrelatedPath));
    }

    [Fact]
    public async Task FindManifestsAsync_IsRootStopsDotNetLookupWithoutStoppingNpmLookup()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var child = root.CreateSubdirectory("src");
        await WriteManifestAsync(root, isNpm: false, "13.3.0");
        await WriteManifestAsync(child, isNpm: false, "13.2.0");
        var rootManifestPath = Path.Combine(child.CreateSubdirectory(".config").FullName, "dotnet-tools.json");
        const string rootManifest = """{"version":1,"isRoot":true,"tools":{"dotnet-ef":{"version":"10.0.0"}}}""";
        await File.WriteAllTextAsync(rootManifestPath, rootManifest);
        var npmPath = await WriteManifestAsync(root, isNpm: true, "13.3.0");
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var manifests = await updater.FindManifestsAsync(child, CancellationToken.None);

        var manifest = Assert.Single(manifests);
        Assert.True(manifest.IsNpm);
        Assert.Equal(npmPath, manifest.File.FullName);
        Assert.Equal(rootManifest, await File.ReadAllTextAsync(rootManifestPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FindManifestsAsync_RespectsGitDirectoryAndWorktreeBoundaries(bool gitFile, bool localManifests)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await WriteManifestAsync(workspace.WorkspaceRoot, isNpm: false, "13.1.0");
        await WriteManifestAsync(workspace.WorkspaceRoot, isNpm: true, "13.1.0");
        var root = workspace.CreateDirectory("repository");
        var gitPath = Path.Combine(root.FullName, ".git");
        if (gitFile)
        {
            await File.WriteAllTextAsync(gitPath, "gitdir: ../worktrees/repository");
        }
        else
        {
            Directory.CreateDirectory(gitPath);
        }

        var expectedPaths = new List<string>();
        if (localManifests)
        {
            expectedPaths.Add(await WriteManifestAsync(root, isNpm: false, "13.3.0"));
            expectedPaths.Add(await WriteManifestAsync(root, isNpm: true, "13.3.0"));
        }

        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());
        var manifests = await updater.FindManifestsAsync(root.CreateSubdirectory("src"), CancellationToken.None);

        Assert.Equal(expectedPaths, manifests.Select(manifest => manifest.File.FullName));
    }

    [Fact]
    public async Task FindManifestsAsync_FindsNearestNpmManifestContainingCli()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        await WriteManifestAsync(root, isNpm: true, "13.1.0");
        var nearestDirectory = root.CreateSubdirectory("packages");
        var nearestPath = await WriteManifestAsync(nearestDirectory, isNpm: true, "13.3.0");
        var child = nearestDirectory.CreateSubdirectory("app");
        var unrelatedPath = Path.Combine(child.FullName, "package.json");
        const string unrelatedContent = """{"name":"app","dependencies":{"typescript":"5.9.0"}}""";
        await File.WriteAllTextAsync(unrelatedPath, unrelatedContent);
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var manifests = await updater.FindManifestsAsync(child, CancellationToken.None);

        Assert.Equal(nearestPath, Assert.Single(manifests).File.FullName);
        Assert.Equal(unrelatedContent, await File.ReadAllTextAsync(unrelatedPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FindManifestsAsync_RejectsLinksOutsideRepository(bool directoryLink, bool gitFile)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.CreateDirectory("repository");
        var gitPath = Path.Combine(root.FullName, ".git");
        if (gitFile)
        {
            await File.WriteAllTextAsync(gitPath, "gitdir: ../worktrees/repository");
        }
        else
        {
            Directory.CreateDirectory(gitPath);
        }
        var external = workspace.CreateDirectory("repository-outside");
        var target = await WriteManifestAsync(external, isNpm: !directoryLink, "13.3.0");
        var original = await File.ReadAllBytesAsync(target);
        var link = Path.Combine(root.FullName, directoryLink ? ".config" : "package.json");
        TestSymlinkHelper.TryCreateSymlink(link, directoryLink ? external.FullName : target, directoryLink);
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.FindManifestsAsync(root, CancellationToken.None));

        Assert.Contains(root.FullName, exception.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateAsync_RechecksLinkTargetsBeforeApplying(bool directoryLink, bool outsideRepository)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var selectedRepository = workspace.CreateDirectory("repository");
        selectedRepository.CreateSubdirectory(".git");
        var originalDirectory = selectedRepository.CreateSubdirectory("original");
        var replacementDirectory = outsideRepository
            ? workspace.CreateDirectory("outside")
            : selectedRepository.CreateSubdirectory("replacement");
        var originalPath = await WriteManifestAsync(originalDirectory, isNpm: !directoryLink, "13.3.0");
        var replacementPath = await WriteManifestAsync(replacementDirectory, isNpm: !directoryLink, "13.3.0");
        var originalBytes = await File.ReadAllBytesAsync(originalPath);
        var replacementBytes = await File.ReadAllBytesAsync(replacementPath);
        var link = Path.Combine(selectedRepository.FullName, directoryLink ? ".config" : "package.json");
        TestSymlinkHelper.TryCreateSymlink(link, directoryLink ? originalDirectory.FullName : originalPath, directoryLink);
        var otherManifest = await WriteManifestAsync(selectedRepository, isNpm: directoryLink, "13.3.0");
        var otherBytes = await File.ReadAllBytesAsync(otherManifest);
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") })
        };
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (_, _) =>
            {
                if (directoryLink)
                {
                    Directory.Delete(link);
                }
                else
                {
                    File.Delete(link);
                }
                TestSymlinkHelper.TryCreateSymlink(link, directoryLink ? replacementDirectory.FullName : replacementPath, directoryLink);
                return true;
            }
        };
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(selectedRepository, CancellationToken.None);

        await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(replacementPath));
        Assert.Equal(otherBytes, await File.ReadAllBytesAsync(otherManifest));
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindManifestsAsync_RejectsUnresolvableLinks(bool directoryLink)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var link = Path.Combine(root.FullName, directoryLink ? ".config" : "package.json");
        TestSymlinkHelper.TryCreateSymlink(link, link, directoryLink);
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.FindManifestsAsync(root, CancellationToken.None));

        Assert.Contains("could not be safely resolved", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_AllowsLinksWithinSymlinkedRepository(bool directoryLink)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.CreateDirectory("repository");
        root.CreateSubdirectory(".git");
        var targetDirectory = root.CreateSubdirectory("manifests");
        var target = await WriteManifestAsync(targetDirectory, isNpm: !directoryLink, "13.3.0");
        var link = Path.Combine(root.FullName, directoryLink ? ".config" : "package.json");
        TestSymlinkHelper.TryCreateSymlink(link, directoryLink ? targetDirectory.FullName : target, directoryLink);
        var alias = Path.Combine(workspace.WorkspaceRoot.FullName, "alias");
        TestSymlinkHelper.TryCreateSymlink(alias, root.FullName);
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") })
        };
        var updater = CreateUpdater(npm, new TestInteractionService());
        var manifests = await updater.FindManifestsAsync(new DirectoryInfo(alias), CancellationToken.None);

        var result = await updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Equal(RepositoryToolUpdateResult.Applied, result);
        var updated = Assert.Single(await updater.FindManifestsAsync(new DirectoryInfo(alias), CancellationToken.None));
        Assert.Equal("13.4.0", Assert.Single(updated.References).Version);
        Assert.NotNull(directoryLink ? new DirectoryInfo(link).LinkTarget : new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesCaseInsensitiveDotNetCliAndPreservesOtherTools()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var configDirectory = root.CreateSubdirectory(".config");
        var manifestPath = Path.Combine(configDirectory.FullName, "dotnet-tools.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "aSpIrE.cLi": {
                  "version": "13.3.0",
                  "commands": ["aspire"],
                  "rollForward": true
                },
                "dotnet-ef": {
                  "version": "10.0.0",
                  "commands": ["dotnet-ef"]
                }
              },
              "custom": { "keep": "unchanged" }
            }
            """);
        var unrelatedPath = Path.Combine(root.FullName, "package.json");
        const string unrelatedContent = """{"name":"app","dependencies":{"typescript":"5.9.0"}}""";
        await File.WriteAllTextAsync(unrelatedPath, unrelatedContent);
        var workingDirectory = root.CreateSubdirectory(Path.Combine("src", "app"));
        var localManifestPath = Path.Combine(workingDirectory.CreateSubdirectory(".config").FullName, "dotnet-tools.json");
        const string localManifest = """{"version":1,"isRoot":false,"tools":{"dotnet-ef":{"version":"9.0.0"}}}""";
        await File.WriteAllTextAsync(localManifestPath, localManifest);
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (directory, package, _, _, _, _, _) =>
            {
                Assert.Equal(configDirectory.FullName, directory.FullName);
                Assert.Equal("Aspire.Cli", package);
                return Task.FromResult<IEnumerable<NuGetPackageCli>>(
                [
                    new() { Id = "aspire.cli", Version = "13.4.0", Source = "test-feed" },
                    new() { Id = "Unrelated.Tool", Version = "99.0.0", Source = "test-feed" }
                ]);
            }
        };
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(CreateUnusedNpmRunner(), interaction);
        var manifests = await updater.FindManifestsAsync(workingDirectory, CancellationToken.None);

        var result = await updater.UpdateAsync(manifests, channel, PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Equal(RepositoryToolUpdateResult.Applied, result);
        Assert.Equal(manifestPath, Assert.Single(manifests).File.FullName);
        await Verify(await File.ReadAllTextAsync(manifestPath), "json");
        Assert.Equal(unrelatedContent, await File.ReadAllTextAsync(unrelatedPath));
        Assert.Equal(localManifest, await File.ReadAllTextAsync(localManifestPath));
        Assert.Single(interaction.BooleanPromptCalls);
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == UpdateCommandStrings.RestoreRepositoryDotNetTool);
    }

    [Fact]
    public async Task UpdateAsync_UsesNpmLatestPreservesDependencyPrefixesAndLeavesLocksUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var manifestPath = Path.Combine(root.FullName, "package.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "name": "app",
              "private": true,
              "scripts": { "start": "aspire run", "other": "echo untouched" },
              "dependencies": {
                "@microsoft/aspire-cli": "13.3.0",
                "other-package": "^2.0.0"
              },
              "devDependencies": {
                "@microsoft/aspire-cli": "^13.3.0",
                "typescript": "~5.9.0"
              },
              "optionalDependencies": {
                "@microsoft/aspire-cli": "~13.3.0"
              },
              "custom": { "keep": ["one", "two"] }
            }
            """);
        var originalLocks = await WriteLockFilesAsync(root);
        var resolutionCalls = 0;
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (package, version, _) =>
            {
                Assert.Equal("@microsoft/aspire-cli", package);
                Assert.Equal("latest", version);
                resolutionCalls++;
                return Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") });
            }
        };
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, _, _, _, _, _, _) => throw new InvalidOperationException("Stable npm updates must not resolve NuGet versions.")
        };
        var channel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Stable, [], cache, new TestFeatures(), NullLogger.Instance);
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        await updater.UpdateAsync(manifests, channel, PromptBinding.CreateDefault(true), CancellationToken.None);

        await Verify(await File.ReadAllTextAsync(manifestPath), "json");
        Assert.Equal(1, resolutionCalls);
        foreach (var (path, original) in originalLocks)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Single(interaction.BooleanPromptCalls);
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == UpdateCommandStrings.RestoreRepositoryNpmTool);
    }

    [Theory]
    [InlineData(false, "13.4.0")]
    [InlineData(true, "13.4.0")]
    [InlineData(false, "14.0.0")]
    [InlineData(true, "14.0.0")]
    public async Task UpdateAsync_ImplicitChannelDoesNotDowngradeOrRewriteCurrentVersions(bool isNpm, string currentVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var manifestPath = await WriteManifestAsync(root, isNpm, currentVersion);
        var originalContent = await File.ReadAllBytesAsync(manifestPath);
        var lockPath = Path.Combine(root.FullName, "package-lock.json");
        const string originalLock = "{ \"lockfileVersion\": 3 }\n";
        await File.WriteAllTextAsync(lockPath, originalLock);
        var npm = CreateUnusedNpmRunner();
        var resolutionCalls = 0;
        if (isNpm)
        {
            npm.IsAvailable = true;
            npm.ResolvePackageAsyncCallback = (package, version, _) =>
            {
                Assert.Equal("@microsoft/aspire-cli", package);
                Assert.Equal("latest", version);
                resolutionCalls++;
                return Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") });
            };
        }

        var interaction = new TestInteractionService
        {
            ConfirmCallback = (_, _) => throw new InvalidOperationException("An unchanged manifest must not prompt.")
        };
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        var result = await updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Equal(RepositoryToolUpdateResult.NoChanges, result);
        Assert.Equal(originalContent, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(originalLock, await File.ReadAllTextAsync(lockPath));
        Assert.Equal(isNpm ? 1 : 0, resolutionCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == UpdateCommandStrings.RepositoryToolsUpToDate);
    }

    [Theory]
    [InlineData(false, "13.3.0")]
    [InlineData(true, "13.3.0")]
    [InlineData(false, "13.5.0-preview.1")]
    [InlineData(true, "13.5.0-preview.1")]
    public async Task UpdateAsync_ExplicitChannelCanSwitchVersions(bool isNpm, string targetVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var manifestPath = await WriteManifestAsync(root, isNpm, "13.4.0");
        var channelName = targetVersion.Contains("preview", StringComparison.Ordinal) ? "daily" : "stable";
        var npm = CreateUnusedNpmRunner();
        var resolutionCalls = 0;
        if (isNpm)
        {
            npm.IsAvailable = true;
            npm.ResolvePackageAsyncCallback = (package, version, _) =>
            {
                Assert.Equal("@microsoft/aspire-cli", package);
                Assert.Equal(channelName == "stable" ? "latest" : targetVersion, version);
                resolutionCalls++;
                return Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse(targetVersion) });
            };
        }

        var updater = CreateUpdater(npm, new TestInteractionService());
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        await updater.UpdateAsync(manifests, CreateChannel(targetVersion, channelName), PromptBinding.CreateDefault(true), CancellationToken.None);

        await Verify(await File.ReadAllTextAsync(manifestPath), "json")
            .UseFileName($"{nameof(RepositoryToolUpdaterTests)}.{nameof(UpdateAsync_ExplicitChannelCanSwitchVersions)}.{(isNpm ? "npm" : "dotnet")}.{channelName}");
        Assert.Equal(isNpm ? 1 : 0, resolutionCalls);
        Assert.False(File.Exists(Path.Combine(root.FullName, "package-lock.json")));
        Assert.False(File.Exists(Path.Combine(root.FullName, "npm-shrinkwrap.json")));
    }

    [Fact]
    public async Task UpdateAsync_NonStableVersionMustExistOnNpmBeforeWritingAnyManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotNetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "13.3.0");
        var originalDotNet = await File.ReadAllBytesAsync(dotNetPath);
        var originalNpm = await File.ReadAllBytesAsync(npmPath);
        var originalLocks = await WriteLockFilesAsync(root);
        var npm = CreateUnusedNpmRunner();
        npm.IsAvailable = true;
        var resolutionCalls = 0;
        npm.ResolvePackageAsyncCallback = (package, version, _) =>
        {
            Assert.Equal("@microsoft/aspire-cli", package);
            Assert.Equal("13.5.0-preview.1", version);
            resolutionCalls++;
            return Task.FromResult<NpmPackageInfo?>(null);
        };
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.UpdateAsync(manifests, CreateChannel("13.5.0-preview.1", "daily"), PromptBinding.CreateDefault(true), CancellationToken.None));

        Assert.Contains("@microsoft/aspire-cli", exception.Message);
        Assert.Contains("13.5.0-preview.1", exception.Message);
        Assert.Equal(1, resolutionCalls);
        Assert.Equal(originalDotNet, await File.ReadAllBytesAsync(dotNetPath));
        Assert.Equal(originalNpm, await File.ReadAllBytesAsync(npmPath));
        foreach (var (path, original) in originalLocks)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Fact]
    public async Task UpdateAsync_NpmUnavailableFailsBeforeWritingAnyManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotNetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "13.3.0");
        var originalDotNet = await File.ReadAllBytesAsync(dotNetPath);
        var originalNpm = await File.ReadAllBytesAsync(npmPath);
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(CreateUnusedNpmRunner(), interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None));

        Assert.Contains("npm", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalDotNet, await File.ReadAllBytesAsync(dotNetPath));
        Assert.Equal(originalNpm, await File.ReadAllBytesAsync(npmPath));
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Fact]
    public async Task UpdateAsync_DecliningDoesNotWriteManifestsOrLocks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotNetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "^13.3.0");
        var originalDotNet = await File.ReadAllBytesAsync(dotNetPath);
        var originalNpm = await File.ReadAllBytesAsync(npmPath);
        var lockPath = Path.Combine(root.FullName, "package-lock.json");
        const string originalLock = "{ \"lockfileVersion\": 3 }\r\n";
        await File.WriteAllTextAsync(lockPath, originalLock);
        var npm = CreateUnusedNpmRunner();
        npm.IsAvailable = true;
        npm.ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") });
        var interaction = new TestInteractionService { ConfirmCallback = (_, _) => false };
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        var result = await updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Equal(RepositoryToolUpdateResult.Declined, result);
        Assert.Equal(originalDotNet, await File.ReadAllBytesAsync(dotNetPath));
        Assert.Equal(originalNpm, await File.ReadAllBytesAsync(npmPath));
        Assert.Equal(originalLock, await File.ReadAllTextAsync(lockPath));
        Assert.Single(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_CancellationDoesNotWriteManifestsOrLocks(bool cancelAtConfirmation)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotNetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "~13.3.0");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(dotNetPath, await File.ReadAllTextAsync(dotNetPath) + "\r\n", encoding);
        await File.WriteAllTextAsync(npmPath, await File.ReadAllTextAsync(npmPath) + "\r\n", encoding);
        var originalFiles = await WriteLockFilesAsync(root);
        foreach (var path in new[] { dotNetPath, npmPath })
        {
            originalFiles.Add(path, await File.ReadAllBytesAsync(path));
        }

        using var cancellation = new CancellationTokenSource();
        var resolutionCalls = 0;
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, cancellationToken) =>
            {
                Assert.Equal(cancellation.Token, cancellationToken);
                resolutionCalls++;
                if (!cancelAtConfirmation)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") });
            }
        };
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (_, _) =>
            {
                Assert.True(cancelAtConfirmation);
                cancellation.Cancel();
                return true;
            }
        };
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, cancellation.Token);
        var channel = CreateChannel("13.4.0");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            updater.UpdateAsync(manifests, channel, PromptBinding.CreateDefault(true), cancellation.Token));

        Assert.Equal(1, resolutionCalls);
        Assert.Equal(cancelAtConfirmation ? 1 : 0, interaction.BooleanPromptCalls.Count);
        foreach (var (path, original) in originalFiles)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false, """{"tools":{"Aspire.Cli":""")]
    [InlineData(true, """{"dependencies":{"@microsoft/aspire-cli":""")]
    [InlineData(false, """{"tools":{"Aspire.Cli":{"version":42}}}""")]
    [InlineData(false, """{"tools":{"Aspire.Cli":{}}}""")]
    [InlineData(false, """{"isRoot":"invalid","tools":{}}""")]
    [InlineData(true, """{"dependencies":{"@microsoft/aspire-cli":42}}""")]
    public async Task FindManifestsAsync_MalformedTargetedJsonReportsManifestPath(bool isNpm, string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var path = Path.Combine(root.FullName, isNpm ? "package.json" : "dotnet-tools.json");
        await File.WriteAllTextAsync(path, content);
        var updater = CreateUpdater(CreateUnusedNpmRunner(), new TestInteractionService());

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            updater.FindManifestsAsync(root, CancellationToken.None));

        Assert.Contains(path, exception.Message);
        Assert.Contains("Failed to read tool manifest", exception.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("https://example.test/aspire-cli.tgz")]
    [InlineData("workspace:*")]
    [InlineData(">=13.3.0 <14.0.0")]
    [InlineData("file:../aspire-cli")]
    [InlineData("npm:another-cli@13.3.0")]
    public async Task UpdateAsync_UnsupportedNpmVersionsWarnWithoutChangingFiles(string version)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var path = await WriteManifestAsync(root, isNpm: true, version);
        var originalContent = await File.ReadAllBytesAsync(path);
        var lockPath = Path.Combine(root.FullName, "package-lock.json");
        await File.WriteAllTextAsync(lockPath, "{}");
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(CreateUnusedNpmRunner(), interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);

        await updater.UpdateAsync(manifests, CreateChannel("13.4.0"), PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Equal(originalContent, await File.ReadAllBytesAsync(path));
        Assert.Equal("{}", await File.ReadAllTextAsync(lockPath));
        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Contains(path, warning.Message);
        Assert.Contains(version, warning.Message);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_DoesNotCreateMissingManifestsOrCliEntries(bool unrelatedManifests)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var originalFiles = new Dictionary<string, byte[]>();
        foreach (var path in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
        {
            originalFiles.Add(path, await File.ReadAllBytesAsync(path));
        }

        if (unrelatedManifests)
        {
            var dotNetPath = Path.Combine(root.FullName, "dotnet-tools.json");
            var npmPath = Path.Combine(root.FullName, "package.json");
            await File.WriteAllTextAsync(dotNetPath, """{"version":1,"isRoot":true,"tools":{"dotnet-ef":{"version":"10.0.0"}}}""");
            await File.WriteAllTextAsync(npmPath, """{"name":"app","devDependencies":{"typescript":"5.9.0"}}""");
            originalFiles.Add(dotNetPath, await File.ReadAllBytesAsync(dotNetPath));
            originalFiles.Add(npmPath, await File.ReadAllBytesAsync(npmPath));
        }

        var interaction = new TestInteractionService();
        var updater = CreateUpdater(CreateUnusedNpmRunner(), interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, _, _, _, _, _, _) => throw new InvalidOperationException("An absent CLI reference must not resolve NuGet packages.")
        };
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);

        await updater.UpdateAsync(manifests, channel, PromptBinding.CreateDefault(true), CancellationToken.None);

        Assert.Empty(manifests);
        Assert.Equal(originalFiles.Keys.Order(), Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, original) in originalFiles)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetUpdateStepAsync_DefersManifestWritesUntilCallback(bool cancelBeforeApply)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotnetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "13.3.0");
        var originals = new Dictionary<string, byte[]>
        {
            [dotnetPath] = await File.ReadAllBytesAsync(dotnetPath),
            [npmPath] = await File.ReadAllBytesAsync(npmPath)
        };
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") })
        };
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(npm, interaction);
        using var cancellation = new CancellationTokenSource();
        var manifests = await updater.FindManifestsAsync(root, cancellation.Token);

        var updateStep = await updater.GetUpdateStepAsync(manifests, CreateChannel("13.4.0"), cancellation.Token);

        Assert.NotNull(updateStep);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedSuccess);
        foreach (var (path, original) in originals)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }

        if (cancelBeforeApply)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(updateStep.Callback);
            foreach (var (path, original) in originals)
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
            Assert.Empty(interaction.DisplayedSuccess);
        }
        else
        {
            await updateStep.Callback();
            var updatedManifests = await updater.FindManifestsAsync(root, cancellation.Token);
            Assert.Equal(2, updatedManifests.Count);
            Assert.All(updatedManifests, manifest => Assert.Equal("13.4.0", Assert.Single(manifest.References).Version));
            Assert.Equal(UpdateCommandStrings.RepositoryToolsUpdated, Assert.Single(interaction.DisplayedSuccess));
        }
        Assert.Empty(interaction.BooleanPromptCalls);
    }

    [Theory]
    [InlineData("""{"dependencies":{"@microsoft/aspire-cli":"13.2.0"}}""")]
    [InlineData("""{"dependencies":{}}""")]
    [InlineData("""{"dependencies":{"@microsoft/aspire-cli":"13.3.0"},"devDependencies":{"@microsoft/aspire-cli":"13.3.0"}}""")]
    [InlineData("""{"devDependencies":{"@microsoft/aspire-cli":"13.3.0"}}""")]
    public async Task GetUpdateStepAsync_ChangedCliReferencesDoNotApplyAnyManifest(string changedManifest)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = CreateRepository(workspace);
        var dotnetPath = await WriteManifestAsync(root, isNpm: false, "13.3.0");
        var npmPath = await WriteManifestAsync(root, isNpm: true, "13.3.0");
        var originalDotnet = await File.ReadAllBytesAsync(dotnetPath);
        var npm = new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.4.0") })
        };
        var interaction = new TestInteractionService();
        var updater = CreateUpdater(npm, interaction);
        var manifests = await updater.FindManifestsAsync(root, CancellationToken.None);
        var updateStep = await updater.GetUpdateStepAsync(manifests, CreateChannel("13.4.0"), CancellationToken.None);
        Assert.NotNull(updateStep);
        await File.WriteAllTextAsync(npmPath, changedManifest);

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(updateStep.Callback);

        Assert.Equal(string.Format(UpdateCommandStrings.ToolManifestChangedFormat, npmPath), exception.Message);
        Assert.Equal(originalDotnet, await File.ReadAllBytesAsync(dotnetPath));
        Assert.Equal(changedManifest, await File.ReadAllTextAsync(npmPath));
        Assert.Empty(interaction.DisplayedSuccess);
    }

    private static DirectoryInfo CreateRepository(TemporaryWorkspace workspace)
    {
        workspace.CreateDirectory(".git");
        return workspace.WorkspaceRoot;
    }

    private static async Task<string> WriteManifestAsync(DirectoryInfo directory, bool isNpm, string version)
    {
        var path = Path.Combine(directory.FullName, isNpm ? "package.json" : "dotnet-tools.json");
        var content = isNpm
            ? $$"""
                {
                  "name": "app",
                  "dependencies": {
                    "@microsoft/aspire-cli": "{{version}}"
                  }
                }
                """
            : $$"""
                {
                  "version": 1,
                  "isRoot": true,
                  "tools": {
                    "Aspire.Cli": {
                      "version": "{{version}}",
                      "commands": ["aspire"]
                    }
                  }
                }
                """;
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static RepositoryToolUpdater CreateUpdater(FakeNpmRunner npmRunner, TestInteractionService interaction)
        => new(npmRunner, interaction, NullLogger<RepositoryToolUpdater>.Instance);

    private static async Task<Dictionary<string, byte[]>> WriteLockFilesAsync(DirectoryInfo directory)
    {
        var packageLockPath = Path.Combine(directory.FullName, "package-lock.json");
        var shrinkwrapPath = Path.Combine(directory.FullName, "npm-shrinkwrap.json");
        await File.WriteAllTextAsync(packageLockPath, "{\r\n  \"lockfileVersion\" : 3\r\n}\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await File.WriteAllTextAsync(shrinkwrapPath, "{ \"name\" : \"original\" }\n");
        return new Dictionary<string, byte[]>
        {
            [packageLockPath] = await File.ReadAllBytesAsync(packageLockPath),
            [shrinkwrapPath] = await File.ReadAllBytesAsync(shrinkwrapPath)
        };
    }

    private static FakeNpmRunner CreateUnusedNpmRunner()
        => new()
        {
            IsAvailable = false,
            ResolvePackageAsyncCallback = (_, _, _) => throw new InvalidOperationException("npm resolution must not run.")
        };

    private static PackageChannel CreateChannel(string version, string? name = null)
    {
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, package, _, _, _, _, _) =>
            {
                Assert.Equal("Aspire.Cli", package);
                return Task.FromResult<IEnumerable<NuGetPackageCli>>(
                [
                    new() { Id = "aspire.cli", Version = version, Source = "test-feed" },
                    new() { Id = "Unrelated.Tool", Version = "99.0.0", Source = "test-feed" }
                ]);
            }
        };
        return name is null
            ? PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance)
            : PackageChannel.CreateExplicitChannel(name, PackageChannelQuality.Both, [], cache, new TestFeatures(), NullLogger.Instance);
    }
}
