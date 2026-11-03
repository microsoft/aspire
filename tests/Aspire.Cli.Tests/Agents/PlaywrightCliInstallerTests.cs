// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class PlaywrightCliInstallerTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);
    private static readonly IReadOnlySet<string> s_emptySkillDirs = new HashSet<string>();

    private static string CreateTestRepoRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task InstallSelectedAsync_GeneratesOnceAndPreservesUnselectedScope(string scope)
    {
        var root = Directory.CreateTempSubdirectory("aspire-selected-playwright-");
        try
        {
            foreach (var scopeName in new[] { "project", "global" })
            {
                foreach (var clientFolder in new[] { ".agents", ".claude", ".github", ".opencode" })
                {
                    var directory = Path.Combine(root.FullName, scopeName, clientFolder, "skills", PlaywrightCliInstaller.PlaywrightCliSkillName);
                    Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "existing skill");
                    await File.WriteAllTextAsync(Path.Combine(directory, "stale.md"), "existing reference");
                }
            }

            var version = SemVersion.Parse("0.1.3", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner { ResolveResult = new NpmPackageInfo { Version = version } };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstallSkillsResult = true,
                OnInstallSkills = stagingRoot =>
                {
                    var directory = Path.Combine(stagingRoot, FakePlaywrightCliRunner.SkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName);
                    Directory.CreateDirectory(Path.Combine(directory, "references"));
                    File.WriteAllText(Path.Combine(directory, "SKILL.md"), "generated skill");
                    File.WriteAllText(Path.Combine(directory, "references", "commands.md"), "generated commands");
                    foreach (var folder in new[] { ".github", ".opencode" })
                    {
                        var generatedDirectory = Path.Combine(stagingRoot, folder, "skills", PlaywrightCliInstaller.PlaywrightCliSkillName);
                        Directory.CreateDirectory(generatedDirectory);
                        File.WriteAllText(Path.Combine(generatedDirectory, "SKILL.md"), "unselected generated skill");
                    }
                }
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(),
                new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
            var sharedDirectory = Path.Combine(root.FullName, scope, ".agents", "skills");
            var destinations = new HashSet<string>
            {
                sharedDirectory,
                sharedDirectory,
                Path.Combine(root.FullName, scope, ".claude", "skills")
            };

            var (status, message) = await installer.InstallSelectedAsync(destinations, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Installed, status);
            Assert.Null(message);
            Assert.Equal(1, npmRunner.PackCallCount);
            Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
            Assert.True(npmRunner.InstallGlobalCalled);
            Assert.NotNull(playwrightRunner.InstallSkillsWorkingDirectory);
            Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(root.FullName, path).Replace('\\', '/'), await File.ReadAllTextAsync(path));
            }
            await Verify(files).UseParameters(scope);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstallSelectedAsync_GenerationFailure_RemovesStagingWithoutWritingDestination()
    {
        var root = Directory.CreateTempSubdirectory("aspire-selected-playwright-");
        try
        {
            var version = SemVersion.Parse("0.1.3", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner { ResolveResult = new NpmPackageInfo { Version = version } };
            var playwrightRunner = new TestPlaywrightCliRunner { InstalledVersion = version, InstallSkillsResult = false };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(),
                new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallSelectedAsync(
                new HashSet<string> { Path.Combine(root.FullName, ".agents", "skills") }, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.NotNull(message);
            Assert.NotNull(playwrightRunner.InstallSkillsWorkingDirectory);
            Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
            Assert.Empty(root.GetFileSystemInfos());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallSelectedAsync_MirrorFailure_ReportsFailureOrPartialSuccess(bool successfulDestination)
    {
        var root = Directory.CreateTempSubdirectory("aspire-selected-playwright-");
        try
        {
            var blockedPath = Path.Combine(root.FullName, "blocked");
            await File.WriteAllTextAsync(blockedPath, "existing file");
            var selectedDirectories = new HashSet<string> { blockedPath };
            var destination = Path.Combine(root.FullName, "skills");
            if (successfulDestination)
            {
                selectedDirectories.Add(destination);
            }
            var version = SemVersion.Parse("0.1.3", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner { ResolveResult = new NpmPackageInfo { Version = version } };
            var playwrightRunner = new TestPlaywrightCliRunner { InstalledVersion = version, InstallSkillsResult = true };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(),
                new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallSelectedAsync(selectedDirectories, CancellationToken.None);

            Assert.Equal(successfulDestination ? PlaywrightInstallStatus.InstalledWithWarnings : PlaywrightInstallStatus.Failed, status);
            Assert.Equal(successfulDestination
                ? AgentCommandStrings.PlaywrightCliInstaller_InstalledWithMirrorWarnings
                : AgentCommandStrings.PlaywrightCliInstaller_FailedToMirrorSkillFiles, message);
            Assert.Equal("existing file", await File.ReadAllTextAsync(blockedPath));
            if (successfulDestination)
            {
                Assert.Equal("# Playwright CLI Skill", await File.ReadAllTextAsync(Path.Combine(destination, PlaywrightCliInstaller.PlaywrightCliSkillName, "SKILL.md")));
            }
            Assert.NotNull(playwrightRunner.InstallSkillsWorkingDirectory);
            Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstallSelectedAsync_MissingGeneratedSkills_ReturnsFailure()
    {
        var root = Directory.CreateTempSubdirectory("aspire-selected-playwright-");
        try
        {
            var version = SemVersion.Parse("0.1.3", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner { ResolveResult = new NpmPackageInfo { Version = version } };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true,
                OnInstallSkills = _ => { }
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(),
                new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallSelectedAsync(
                new HashSet<string> { Path.Combine(root.FullName, "skills") }, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.Equal(AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles, message);
            Assert.Empty(root.GetFileSystemInfos());
            Assert.NotNull(playwrightRunner.InstallSkillsWorkingDirectory);
            Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallSelectedAsync_ReadOnlyTemporaryFile_DoesNotOverrideInstallationResult(bool tarballIsReadOnly)
    {
        var root = Directory.CreateTempSubdirectory("aspire-selected-playwright-");
        string? readOnlyFile = null;
        string? stagingRoot = null;
        try
        {
            var version = SemVersion.Parse("0.1.3", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner { ResolveResult = new NpmPackageInfo { Version = version } };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstallSkillsResult = true,
                OnInstallSkills = directory =>
                {
                    stagingRoot = directory;
                    var skillDirectory = Path.Combine(directory, FakePlaywrightCliRunner.SkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName);
                    Directory.CreateDirectory(skillDirectory);
                    var skillFile = Path.Combine(skillDirectory, "SKILL.md");
                    File.WriteAllText(skillFile, "# Playwright CLI Skill");
                    readOnlyFile = tarballIsReadOnly ? npmRunner.PackResult : skillFile;
                    Assert.NotNull(readOnlyFile);
                    File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
                }
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(),
                new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
            var destination = Path.Combine(root.FullName, "skills");

            var (status, message) = await installer.InstallSelectedAsync(new HashSet<string> { destination }, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Installed, status);
            Assert.Null(message);
            var installedFile = Path.Combine(destination, PlaywrightCliInstaller.PlaywrightCliSkillName, "SKILL.md");
            Assert.Equal("# Playwright CLI Skill", await File.ReadAllTextAsync(installedFile));
            File.SetAttributes(installedFile, FileAttributes.Normal);
        }
        finally
        {
            if (readOnlyFile is not null && File.Exists(readOnlyFile))
            {
                File.SetAttributes(readOnlyFile, FileAttributes.Normal);
                Directory.Delete(Path.GetDirectoryName(readOnlyFile)!, recursive: true);
            }
            if (stagingRoot is not null && Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenNpmResolveReturnsNull_ReturnsErrorMessage()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = null
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.NotNull(message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenAlreadyInstalledAtSameVersion_SkipsInstallAndInstallsSkills()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, _) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Installed, status);
            Assert.True(playwrightRunner.InstallSkillsCalled);
            Assert.False(npmRunner.PackCalled);
            Assert.False(npmRunner.InstallGlobalCalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_PassesRepositoryRootAsWorkingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.True(playwrightRunner.InstallSkillsCalled);
            Assert.Equal(tempDir, playwrightRunner.InstallSkillsWorkingDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenNewerVersionInstalled_SkipsInstallAndInstallsSkills()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var targetVersion = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var installedVersion = SemVersion.Parse("0.2.0", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = targetVersion }
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = installedVersion,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, _) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Installed, status);
            Assert.True(playwrightRunner.InstallSkillsCalled);
            Assert.False(npmRunner.PackCalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenPackFails_ReturnsErrorMessage()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version },
                PackResult = null
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.NotNull(message);
            Assert.True(npmRunner.PackCalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenIntegrityCheckPasses_InstallsGlobally()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        var content = new byte[] { 10, 20, 30, 40, 50 };
        await File.WriteAllBytesAsync(tarballPath, content);

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version },
                PackResult = tarballPath,
                InstallGlobalResult = true
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var repoRoot = CreateTestRepoRoot();
            try
            {
                var (status, _) = await installer.InstallAsync(repoRoot, s_emptySkillDirs, CancellationToken.None);

                Assert.Equal(PlaywrightInstallStatus.Installed, status);
                Assert.True(npmRunner.InstallGlobalCalled);
                Assert.True(playwrightRunner.InstallSkillsCalled);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallAsync_UsesDownloadedTarballDigestForProvenance()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Directory.CreateTempSubdirectory("test-playwright-").FullName;
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        var content = new byte[] { 10, 20, 30, 40, 50 };
        await File.WriteAllBytesAsync(tarballPath, content);
        var expectedIntegrity = $"sha512-{Convert.ToBase64String(SHA512.HashData(content))}";

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version },
                PackResult = tarballPath,
                InstallGlobalResult = true
            };
            var provenanceChecker = new TestNpmProvenanceChecker();
            var playwrightRunner = new TestPlaywrightCliRunner { InstallSkillsResult = true };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
            var repoRoot = CreateTestRepoRoot();

            try
            {
                var (status, _) = await installer.InstallAsync(repoRoot, s_emptySkillDirs, CancellationToken.None);

                Assert.Equal(PlaywrightInstallStatus.Installed, status);
                Assert.Equal(expectedIntegrity, provenanceChecker.CapturedSriIntegrity);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGlobalInstallFails_ReturnsErrorMessage()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        var content = new byte[] { 10, 20, 30 };
        await File.WriteAllBytesAsync(tarballPath, content);

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version },
                PackResult = tarballPath,
                InstallGlobalResult = false
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var repoRoot = CreateTestRepoRoot();
            try
            {
                var (status, message) = await installer.InstallAsync(repoRoot, s_emptySkillDirs, CancellationToken.None);

                Assert.Equal(PlaywrightInstallStatus.Failed, status);
                Assert.NotNull(message);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenOlderVersionInstalled_PerformsUpgrade()
    {
        var targetVersion = SemVersion.Parse("0.1.2", SemVersionStyles.Strict);
        var installedVersion = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        var content = new byte[] { 99, 100 };
        await File.WriteAllBytesAsync(tarballPath, content);

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = targetVersion },
                PackResult = tarballPath,
                InstallGlobalResult = true
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = installedVersion,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var repoRoot = CreateTestRepoRoot();
            try
            {
                var (status, _) = await installer.InstallAsync(repoRoot, s_emptySkillDirs, CancellationToken.None);

                Assert.Equal(PlaywrightInstallStatus.Installed, status);
                Assert.True(npmRunner.PackCalled);
                Assert.True(npmRunner.InstallGlobalCalled);
                Assert.True(playwrightRunner.InstallSkillsCalled);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ComputeIntegrity_ReturnsSha512SriValue()
    {
        var tempDir = Directory.CreateTempSubdirectory("test-playwright-").FullName;
        var tempPath = Path.Combine(tempDir, "package.tgz");
        try
        {
            var content = "test content for hashing"u8.ToArray();
            File.WriteAllBytes(tempPath, content);

            var hash = SHA512.HashData(content);
            var expectedIntegrity = $"sha512-{Convert.ToBase64String(hash)}";

            Assert.Equal(expectedIntegrity, PlaywrightCliInstaller.ComputeIntegrity(tempPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WorkflowRefValidator_AcceptsBothTagFormats()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.1.7", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var provenanceChecker = new TestNpmProvenanceChecker();
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.True(provenanceChecker.ProvenanceCalled);
            Assert.NotNull(provenanceChecker.CapturedValidateWorkflowRef);

            // Accept tags without 'v' prefix (0.1.7+)
            Assert.True(WorkflowRefInfo.TryParse($"refs/tags/{version}", out var refWithout));
            Assert.True(provenanceChecker.CapturedValidateWorkflowRef(refWithout!));

            // Accept tags with 'v' prefix (pre-0.1.7)
            Assert.True(WorkflowRefInfo.TryParse($"refs/tags/v{version}", out var refWith));
            Assert.True(provenanceChecker.CapturedValidateWorkflowRef(refWith!));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenProvenanceCheckFails_ReturnsErrorMessage()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var provenanceChecker = new TestNpmProvenanceChecker { ProvenanceOutcome = ProvenanceVerificationOutcome.SourceRepositoryMismatch };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.NotNull(message);
            Assert.True(provenanceChecker.ProvenanceCalled);
            Assert.True(npmRunner.PackCalled);
            Assert.False(npmRunner.InstallGlobalCalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenValidationDisabled_SkipsAllValidationChecks()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        await File.WriteAllBytesAsync(tarballPath, [10, 20, 30]);

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version },
                PackResult = tarballPath
            };
            var provenanceChecker = new TestNpmProvenanceChecker { ProvenanceOutcome = ProvenanceVerificationOutcome.AttestationFetchFailed };
            var playwrightRunner = new TestPlaywrightCliRunner { InstallSkillsResult = true };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PlaywrightCliInstaller.DisablePackageValidationKey] = "true"
                })
                .Build();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), configuration, NullLogger<PlaywrightCliInstaller>.Instance);

            var repoRoot = CreateTestRepoRoot();
            try
            {
                var (status, _) = await installer.InstallAsync(repoRoot, s_emptySkillDirs, CancellationToken.None);

                Assert.Equal(PlaywrightInstallStatus.Installed, status);
                Assert.False(provenanceChecker.ProvenanceCalled);
                Assert.True(npmRunner.PackCalled);
                Assert.True(npmRunner.InstallGlobalCalled);
            }
            finally
            {
                if (Directory.Exists(repoRoot))
                {
                    Directory.Delete(repoRoot, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenVersionOverrideConfigured_UsesOverrideVersion()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.2.0", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PlaywrightCliInstaller.VersionOverrideKey] = "0.2.0"
                })
                .Build();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), configuration, NullLogger<PlaywrightCliInstaller>.Instance);

            // PackAsync returns null by default — we only care about the resolved range
            await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal("0.2.0", npmRunner.ResolvedVersionRange);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(">=0.2.0")]
    [InlineData("latest")]
    [InlineData("0.2")]
    [InlineData("not-a-version")]
    [InlineData("v0.2.0")]
    public async Task InstallAsync_WhenVersionOverrideIsNotStrictSemVer_ReturnsFailed(string invalidVersion)
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = SemVersion.Parse("0.2.0", SemVersionStyles.Strict) }
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PlaywrightCliInstaller.VersionOverrideKey] = invalidVersion
                })
                .Build();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), configuration, NullLogger<PlaywrightCliInstaller>.Instance);

            var (status, message) = await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightInstallStatus.Failed, status);
            Assert.NotNull(message);
            Assert.Contains(invalidVersion, message);
            Assert.Null(npmRunner.ResolvedVersionRange);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenNoVersionOverride_UsesDefaultRange()
    {
        var tempDir = CreateTestRepoRoot();

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(_context.Clients, npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            // PackAsync returns null by default — we only care about the resolved range
            await installer.InstallAsync(tempDir, s_emptySkillDirs, CancellationToken.None);

            Assert.Equal(PlaywrightCliInstaller.VersionRange, npmRunner.ResolvedVersionRange);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_MirrorsSkillFilesToOtherAgentEnvironments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-mirror-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true,
                // Simulate playwright-cli writing to .claude/skills/ during install
                OnInstallSkills = repoRoot =>
                {
                    var skillDir = Path.Combine(repoRoot, ".claude", "skills", "playwright-cli");
                    Directory.CreateDirectory(skillDir);
                    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Playwright CLI Skill");
                    Directory.CreateDirectory(Path.Combine(skillDir, "subdir"));
                    File.WriteAllText(Path.Combine(skillDir, "subdir", "extra.md"), "Extra content");
                }
            };
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };

            var installer = new PlaywrightCliInstaller(
                _context.Clients,
                npmRunner, new TestNpmProvenanceChecker(), playwrightRunner,
                new TestInteractionService(), new ConfigurationBuilder().Build(),
                NullLogger<PlaywrightCliInstaller>.Instance);

            var selectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(".claude", "skills"),
                Path.Combine(".agents", "skills")
            };
            await installer.InstallAsync(tempDir, selectedDirs, CancellationToken.None);

            Assert.Equal(
                [
                    Path.Combine(".agents", "skills", "playwright-cli", "SKILL.md"),
                    Path.Combine(".agents", "skills", "playwright-cli", "subdir", "extra.md"),
                    Path.Combine(".claude", "skills", "playwright-cli", "SKILL.md"),
                    Path.Combine(".claude", "skills", "playwright-cli", "subdir", "extra.md")
                ],
                Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(tempDir, path)).Order(StringComparer.Ordinal));
            Assert.Equal("# Playwright CLI Skill", await File.ReadAllTextAsync(Path.Combine(tempDir, ".agents", "skills", "playwright-cli", "SKILL.md")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SyncDirectory_RemovesExtraFilesInTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-sync-test-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(tempDir, "source");
        var targetDir = Path.Combine(tempDir, "target");

        try
        {
            // Set up source with one file
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "keep.md"), "keep");

            // Set up target with an extra file that should be removed
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "keep.md"), "old content");
            File.WriteAllText(Path.Combine(targetDir, "stale.md"), "should be removed");
            Directory.CreateDirectory(Path.Combine(targetDir, "stale-dir"));
            File.WriteAllText(Path.Combine(targetDir, "stale-dir", "old.md"), "should be removed");

            PlaywrightCliInstaller.SyncDirectory(sourceDir, targetDir);

            // Source file should be copied
            Assert.Equal("keep", File.ReadAllText(Path.Combine(targetDir, "keep.md")));

            // Stale files and directories should be removed
            Assert.False(File.Exists(Path.Combine(targetDir, "stale.md")));
            Assert.False(Directory.Exists(Path.Combine(targetDir, "stale-dir")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenOnlyStandardSelected_CleansUpUnselectedLocations()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-cleanup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true,
                // Simulate playwright-cli writing to .claude/skills/ during install
                OnInstallSkills = repoRoot =>
                {
                    var skillDir = Path.Combine(repoRoot, ".claude", "skills", "playwright-cli");
                    Directory.CreateDirectory(skillDir);
                    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Playwright CLI Skill");
                }
            };
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };

            var installer = new PlaywrightCliInstaller(
                _context.Clients,
                npmRunner, new TestNpmProvenanceChecker(), playwrightRunner,
                new TestInteractionService(), new ConfigurationBuilder().Build(),
                NullLogger<PlaywrightCliInstaller>.Instance);

            // Only select Standard (.agents/skills)
            var selectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(".agents", "skills")
            };
            await installer.InstallAsync(tempDir, selectedDirs, CancellationToken.None);

            // Skill should be mirrored to the selected location
            Assert.True(File.Exists(Path.Combine(tempDir, ".agents", "skills", "playwright-cli", "SKILL.md")));

            // .claude/skills/playwright-cli should be cleaned up (created by playwright-cli, not selected)
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".claude", "skills", "playwright-cli")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCleaningUp_DoesNotDeletePreExistingDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-preexist-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Pre-existing .claude/skills/playwright-cli (user already had this)
            var preExistingDir = Path.Combine(tempDir, ".claude", "skills", "playwright-cli");
            Directory.CreateDirectory(preExistingDir);
            await File.WriteAllTextAsync(Path.Combine(preExistingDir, "SKILL.md"), "# User's existing skill");

            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true
            };
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };

            var installer = new PlaywrightCliInstaller(
                _context.Clients,
                npmRunner, new TestNpmProvenanceChecker(), playwrightRunner,
                new TestInteractionService(), new ConfigurationBuilder().Build(),
                NullLogger<PlaywrightCliInstaller>.Instance);

            // Only select Standard — .claude/skills is NOT selected
            var selectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(".agents", "skills")
            };
            await installer.InstallAsync(tempDir, selectedDirs, CancellationToken.None);

            // .claude/skills/playwright-cli should NOT be deleted because it pre-existed
            Assert.True(Directory.Exists(preExistingDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCleaningUp_RemovesEmptyParentDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-emptydir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true,
                // Simulate playwright-cli writing to .claude/skills/ during install
                OnInstallSkills = repoRoot =>
                {
                    var skillDir = Path.Combine(repoRoot, ".claude", "skills", "playwright-cli");
                    Directory.CreateDirectory(skillDir);
                    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Playwright CLI Skill");
                }
            };
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version }
            };

            var installer = new PlaywrightCliInstaller(
                _context.Clients,
                npmRunner, new TestNpmProvenanceChecker(), playwrightRunner,
                new TestInteractionService(), new ConfigurationBuilder().Build(),
                NullLogger<PlaywrightCliInstaller>.Instance);

            var selectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(".agents", "skills")
            };
            await installer.InstallAsync(tempDir, selectedDirs, CancellationToken.None);

            // The entire .claude/skills/ tree should be gone (empty after playwright-cli removal)
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".claude", "skills")));
            // .claude itself should also be gone if it's empty
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".claude")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoveEmptyParentDirectories_RemovesEmptyParentsUpToStopDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-rmparent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create .claude/skills/playwright-cli (3 levels), then remove playwright-cli
            var skillDir = Path.Combine(tempDir, ".claude", "skills", "playwright-cli");
            Directory.CreateDirectory(skillDir);
            Directory.Delete(skillDir);

            // Now .claude/skills is empty — RemoveEmptyParentDirectories should clean up
            PlaywrightCliInstaller.RemoveEmptyParentDirectories(
                skillDir,
                tempDir,
                Path.Combine(".claude", "skills"));

            Assert.False(Directory.Exists(Path.Combine(tempDir, ".claude", "skills")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".claude")));
            Assert.True(Directory.Exists(tempDir)); // stop dir should NOT be deleted
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoveEmptyParentDirectories_StopsAtNonEmptyParent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-rmparent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create .claude/skills/playwright-cli and .claude/skills/other-skill
            var skillDir = Path.Combine(tempDir, ".claude", "skills", "playwright-cli");
            Directory.CreateDirectory(skillDir);
            Directory.CreateDirectory(Path.Combine(tempDir, ".claude", "skills", "other-skill"));
            Directory.Delete(skillDir);

            PlaywrightCliInstaller.RemoveEmptyParentDirectories(
                skillDir,
                tempDir,
                Path.Combine(".claude", "skills"));

            // .claude/skills should still exist because it has other-skill
            Assert.True(Directory.Exists(Path.Combine(tempDir, ".claude", "skills")));
            Assert.True(Directory.Exists(Path.Combine(tempDir, ".claude", "skills", "other-skill")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoveEmptyParentDirectories_NeverDeletesStopDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-rmparent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create a single level: .agents/playwright-cli
            var skillDir = Path.Combine(tempDir, ".agents", "playwright-cli");
            Directory.CreateDirectory(skillDir);
            Directory.Delete(skillDir);

            PlaywrightCliInstaller.RemoveEmptyParentDirectories(
                skillDir,
                tempDir,
                ".agents");

            // .agents should be removed (it's empty and not the stop dir)
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".agents")));
            // tempDir (stop dir) must never be removed
            Assert.True(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoveEmptyParentDirectories_RespectsMaxDepthLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-rmparent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create a deep path: a/b/c/d/e
            var deepDir = Path.Combine(tempDir, "a", "b", "c", "d", "e");
            Directory.CreateDirectory(deepDir);
            Directory.Delete(deepDir);
            // d, c, b, a are all empty

            // Relative path "a/b" means maxDepth = 3 (2 segments + 1)
            // Starting from e's path, it should walk up: d (1), c (2), b (3) — stops there
            PlaywrightCliInstaller.RemoveEmptyParentDirectories(
                deepDir,
                tempDir,
                Path.Combine("a", "b"));

            // d, c, b should be removed (within maxDepth)
            Assert.False(Directory.Exists(Path.Combine(tempDir, "a", "b", "c", "d")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "a", "b", "c")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "a", "b")));
            // a should still exist (maxDepth exceeded)
            Assert.True(Directory.Exists(Path.Combine(tempDir, "a")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    public void Dispose() => _context.Dispose();

    private sealed class TestNpmRunner : INpmRunner
    {
        public bool IsAvailable => true;

        public NpmPackageInfo? ResolveResult { get; set; }
        public string? PackResult { get; set; } = string.Empty;
        public bool InstallGlobalResult { get; set; } = true;

        public bool PackCalled { get; private set; }
        public int PackCallCount { get; private set; }
        public bool InstallGlobalCalled { get; private set; }
        public string? ResolvedVersionRange { get; private set; }

        public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
        {
            ResolvedVersionRange = versionRange;
            return Task.FromResult(ResolveResult);
        }

        public Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
        {
            PackCalled = true;
            PackCallCount++;
            if (PackResult == string.Empty)
            {
                PackResult = Path.Combine(outputDirectory, "package.tgz");
                File.WriteAllBytes(PackResult, [1, 2, 3]);
            }

            return Task.FromResult(PackResult);
        }

        public Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
        {
            InstallGlobalCalled = true;
            return Task.FromResult(InstallGlobalResult);
        }
    }

    private sealed class TestNpmProvenanceChecker : INpmProvenanceChecker
    {
        public ProvenanceVerificationOutcome ProvenanceOutcome { get; set; } = ProvenanceVerificationOutcome.Verified;
        public bool ProvenanceCalled { get; private set; }
        public Func<WorkflowRefInfo, bool>? CapturedValidateWorkflowRef { get; private set; }
        public string? CapturedSriIntegrity { get; private set; }

        public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, string? sriIntegrity, CancellationToken cancellationToken)
        {
            ProvenanceCalled = true;
            CapturedValidateWorkflowRef = validateWorkflowRef;
            CapturedSriIntegrity = sriIntegrity;
            return Task.FromResult(new ProvenanceVerificationResult
            {
                Outcome = ProvenanceOutcome,
                Provenance = ProvenanceOutcome is ProvenanceVerificationOutcome.Verified
                    ? new NpmProvenanceData { SourceRepository = expectedSourceRepository }
                    : new NpmProvenanceData()
            });
        }
    }

    private sealed class TestPlaywrightCliRunner : IPlaywrightCliRunner
    {
        public SemVersion? InstalledVersion { get; set; }
        public bool InstallSkillsResult { get; set; }
        public bool InstallSkillsCalled { get; private set; }
        public int InstallSkillsCallCount { get; private set; }
        public string? InstallSkillsWorkingDirectory { get; private set; }
        public Action<string>? OnInstallSkills { get; set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
            => Task.FromResult(InstalledVersion);

        public Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            InstallSkillsCalled = true;
            InstallSkillsCallCount++;
            InstallSkillsWorkingDirectory = workingDirectory;
            if (OnInstallSkills is { } installSkills)
            {
                installSkills(workingDirectory);
            }
            else if (InstallSkillsResult)
            {
                var directory = Path.Combine(workingDirectory, FakePlaywrightCliRunner.SkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), "# Playwright CLI Skill");
            }
            return Task.FromResult(InstallSkillsResult);
        }
    }
}
