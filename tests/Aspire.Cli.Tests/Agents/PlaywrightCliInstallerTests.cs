// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class PlaywrightCliInstallerTests
{
    private static AgentEnvironmentScanContext CreateTestContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new AgentEnvironmentScanContext
        {
            WorkingDirectory = new DirectoryInfo(tempDir),
            RepositoryRoot = new DirectoryInfo(tempDir)
        };
    }

    [Fact]
    public async Task InstallAsync_WhenNpmResolveReturnsNull_ReturnsFalse()
    {
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = null
        };
        var playwrightRunner = new TestPlaywrightCliRunner();
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task InstallAsync_WhenAlreadyInstalledAtSameVersion_SkipsInstallAndInstallsSkills()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
        };
        var playwrightRunner = new TestPlaywrightCliRunner
        {
            InstalledVersion = version,
            InstallSkillsResult = true
        };
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.True(result);
        Assert.True(playwrightRunner.InstallSkillsCalled);
        Assert.False(npmRunner.PackCalled);
        Assert.False(npmRunner.InstallGlobalCalled);
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
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var context = new AgentEnvironmentScanContext
            {
                WorkingDirectory = new DirectoryInfo(Path.GetTempPath()),
                RepositoryRoot = new DirectoryInfo(tempDir)
            };

            await installer.InstallAsync(context, CancellationToken.None);

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
        var targetVersion = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var installedVersion = SemVersion.Parse("0.2.0", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = targetVersion, Integrity = "sha512-abc123" }
        };
        var playwrightRunner = new TestPlaywrightCliRunner
        {
            InstalledVersion = installedVersion,
            InstallSkillsResult = true
        };
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.True(result);
        Assert.True(playwrightRunner.InstallSkillsCalled);
        Assert.False(npmRunner.PackCalled);
    }

    [Fact]
    public async Task InstallAsync_WhenPackFails_ReturnsFalse()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" },
            PackResult = null
        };
        var playwrightRunner = new TestPlaywrightCliRunner();
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.False(result);
        Assert.True(npmRunner.PackCalled);
    }

    [Fact]
    public async Task InstallAsync_WhenIntegrityCheckFails_ReturnsFalse()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        // Create a temp file with known content and a non-matching hash
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        await File.WriteAllBytesAsync(tarballPath, [1, 2, 3]);

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-definitelyWrongHash" },
                PackResult = tarballPath
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

            Assert.False(result);
            Assert.False(npmRunner.InstallGlobalCalled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
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

        // Compute the correct SRI hash for the content
        var hash = SHA512.HashData(content);
        var integrity = $"sha512-{Convert.ToBase64String(hash)}";

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = integrity },
                PackResult = tarballPath,
                InstallGlobalResult = true
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

            Assert.True(result);
            Assert.True(npmRunner.InstallGlobalCalled);
            Assert.True(playwrightRunner.InstallSkillsCalled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGlobalInstallFails_ReturnsFalse()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        var content = new byte[] { 10, 20, 30 };
        await File.WriteAllBytesAsync(tarballPath, content);

        var hash = SHA512.HashData(content);
        var integrity = $"sha512-{Convert.ToBase64String(hash)}";

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = integrity },
                PackResult = tarballPath,
                InstallGlobalResult = false
            };
            var playwrightRunner = new TestPlaywrightCliRunner();
            var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

            Assert.False(result);
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

        var hash = SHA512.HashData(content);
        var integrity = $"sha512-{Convert.ToBase64String(hash)}";

        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = targetVersion, Integrity = integrity },
                PackResult = tarballPath,
                InstallGlobalResult = true
            };
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = installedVersion,
                InstallSkillsResult = true
            };
            var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

            var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

            Assert.True(result);
            Assert.True(npmRunner.PackCalled);
            Assert.True(npmRunner.InstallGlobalCalled);
            Assert.True(playwrightRunner.InstallSkillsCalled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void VerifyIntegrity_WithMatchingHash_ReturnsTrue()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var content = "test content for hashing"u8.ToArray();
            File.WriteAllBytes(tempPath, content);

            var hash = SHA512.HashData(content);
            var integrity = $"sha512-{Convert.ToBase64String(hash)}";

            Assert.True(PlaywrightCliInstaller.VerifyIntegrity(tempPath, integrity));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void VerifyIntegrity_WithNonMatchingHash_ReturnsFalse()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "some content");

            Assert.False(PlaywrightCliInstaller.VerifyIntegrity(tempPath, "sha512-wronghash"));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void VerifyIntegrity_WithNonSha512Prefix_ReturnsFalse()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "some content");

            Assert.False(PlaywrightCliInstaller.VerifyIntegrity(tempPath, "sha256-somehash"));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenProvenanceCheckFails_ReturnsFalse()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
        };
        var provenanceChecker = new TestNpmProvenanceChecker { ProvenanceOutcome = ProvenanceVerificationOutcome.SourceRepositoryMismatch };
        var playwrightRunner = new TestPlaywrightCliRunner();
        var installer = new PlaywrightCliInstaller(npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.False(result);
        Assert.True(provenanceChecker.ProvenanceCalled);
        Assert.False(npmRunner.PackCalled);
    }

    [Fact]
    public async Task InstallAsync_WhenValidationDisabled_SkipsAllValidationChecks()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tarballPath = Path.Combine(tempDir, "package.tgz");
        await File.WriteAllBytesAsync(tarballPath, [10, 20, 30]);

        // Use a mismatched integrity hash — validation is disabled so it should still succeed.
        try
        {
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-wronghash" },
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
            var installer = new PlaywrightCliInstaller(npmRunner, provenanceChecker, playwrightRunner, new TestInteractionService(), configuration, NullLogger<PlaywrightCliInstaller>.Instance);

            var result = await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

            Assert.True(result);
            Assert.False(provenanceChecker.ProvenanceCalled);
            Assert.True(npmRunner.PackCalled);
            Assert.True(npmRunner.InstallGlobalCalled);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenVersionOverrideConfigured_UsesOverrideVersion()
    {
        var version = SemVersion.Parse("0.2.0", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
        };
        var playwrightRunner = new TestPlaywrightCliRunner();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.VersionOverrideKey] = "0.2.0"
            })
            .Build();
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), configuration, NullLogger<PlaywrightCliInstaller>.Instance);

        await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.Equal("0.2.0", npmRunner.ResolvedVersionRange);
    }

    [Fact]
    public async Task InstallAsync_WhenNoVersionOverride_UsesDefaultRange()
    {
        var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
        var npmRunner = new TestNpmRunner
        {
            ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
        };
        var playwrightRunner = new TestPlaywrightCliRunner();
        var installer = new PlaywrightCliInstaller(npmRunner, new TestNpmProvenanceChecker(), playwrightRunner, new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);

        await installer.InstallAsync(CreateTestContext(), CancellationToken.None);

        Assert.Equal(PlaywrightCliInstaller.VersionRange, npmRunner.ResolvedVersionRange);
    }

    [Fact]
    public async Task InstallAsync_MirrorsSkillFilesToOtherAgentEnvironments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-mirror-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Set up the primary skill directory with a skill file (simulating playwright-cli output)
            var primarySkillDir = Path.Combine(tempDir, ".claude", "skills", "playwright-cli");
            Directory.CreateDirectory(primarySkillDir);
            await File.WriteAllTextAsync(Path.Combine(primarySkillDir, "SKILL.md"), "# Playwright CLI Skill");
            Directory.CreateDirectory(Path.Combine(primarySkillDir, "subdir"));
            await File.WriteAllTextAsync(Path.Combine(primarySkillDir, "subdir", "extra.md"), "Extra content");

            var version = SemVersion.Parse("0.1.1", SemVersionStyles.Strict);
            var playwrightRunner = new TestPlaywrightCliRunner
            {
                InstalledVersion = version,
                InstallSkillsResult = true
            };
            var npmRunner = new TestNpmRunner
            {
                ResolveResult = new NpmPackageInfo { Version = version, Integrity = "sha512-abc123" }
            };

            var installer = new PlaywrightCliInstaller(
                npmRunner, new TestNpmProvenanceChecker(), playwrightRunner,
                new TestInteractionService(), new ConfigurationBuilder().Build(),
                NullLogger<PlaywrightCliInstaller>.Instance);

            var context = new AgentEnvironmentScanContext
            {
                WorkingDirectory = new DirectoryInfo(tempDir),
                RepositoryRoot = new DirectoryInfo(tempDir)
            };
            context.AddSkillBaseDirectory(Path.Combine(".claude", "skills"));
            context.AddSkillBaseDirectory(Path.Combine(".github", "skills"));
            context.AddSkillBaseDirectory(Path.Combine(".opencode", "skill"));

            await installer.InstallAsync(context, CancellationToken.None);

            // Verify files were mirrored to .github/skills/playwright-cli/
            Assert.True(File.Exists(Path.Combine(tempDir, ".github", "skills", "playwright-cli", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".github", "skills", "playwright-cli", "subdir", "extra.md")));
            Assert.Equal("# Playwright CLI Skill", await File.ReadAllTextAsync(Path.Combine(tempDir, ".github", "skills", "playwright-cli", "SKILL.md")));

            // Verify files were mirrored to .opencode/skill/playwright-cli/
            Assert.True(File.Exists(Path.Combine(tempDir, ".opencode", "skill", "playwright-cli", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".opencode", "skill", "playwright-cli", "subdir", "extra.md")));
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

    private sealed class TestNpmRunner : INpmRunner
    {
        public NpmPackageInfo? ResolveResult { get; set; }
        public string? PackResult { get; set; }
        public bool AuditResult { get; set; } = true;
        public bool InstallGlobalResult { get; set; } = true;

        public bool PackCalled { get; private set; }
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
            return Task.FromResult(PackResult);
        }

        public Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
            => Task.FromResult(AuditResult);

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

        public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, CancellationToken cancellationToken, string? sriIntegrity = null)
        {
            ProvenanceCalled = true;
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
        public string? InstallSkillsWorkingDirectory { get; private set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
            => Task.FromResult(InstalledVersion);

        public Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            InstallSkillsCalled = true;
            InstallSkillsWorkingDirectory = workingDirectory;
            return Task.FromResult(InstallSkillsResult);
        }
    }
}
