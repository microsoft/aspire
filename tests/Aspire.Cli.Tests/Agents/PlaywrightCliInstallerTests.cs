// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class PlaywrightCliInstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InstallAsync_WhenNpmIsUnavailable_DoesNotProbeOrInstall()
    {
        var npmRunner = new FakeNpmRunner { IsAvailable = false };
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Skipped, result.Status);
        Assert.Equal(AgentSkillInstallerStrings.PlaywrightNpmRequired, result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenNpmResolveReturnsNull_ReturnsErrorMessage()
    {
        var npmRunner = new FakeNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(0, npmRunner.PackCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Theory]
    [InlineData("0.1.7")]
    [InlineData("0.2.0")]
    public async Task InstallAsync_WhenSuitableVersionIsInstalled_SkipsInstallAndGeneratesSkills(string installedVersion)
    {
        var npmRunner = CreateNpmRunner();
        var provenanceChecker = new FakeNpmProvenanceChecker();
        var playwrightRunner = new FakePlaywrightCliRunner
        {
            InstalledVersion = SemVersion.Parse(installedVersion, SemVersionStyles.Strict)
        };
        var installer = CreateInstaller(npmRunner, playwrightRunner, provenanceChecker);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, npmRunner.ResolveCallCount);
        Assert.Equal(1, playwrightRunner.GetVersionCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.Equal(0, npmRunner.PackCallCount);
        Assert.Equal(0, npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, provenanceChecker.CallCount);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenPackFails_ReturnsErrorMessage()
    {
        var npmRunner = CreateNpmRunner();
        npmRunner.PackResult = null;
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(0, npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(npmRunner.PackOutputDirectory));
    }

    [Fact]
    public async Task InstallAsync_VerifiesDownloadedTarballBeforeInstallingGlobally()
    {
        var npmRunner = CreateNpmRunner();
        npmRunner.TarballContent = [10, 20, 30, 40, 50];
        var provenanceChecker = new FakeNpmProvenanceChecker();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner, provenanceChecker);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(1, provenanceChecker.CallCount);
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.Equal(npmRunner.PackedTarballPath, npmRunner.InstalledTarballPath);
        Assert.Equal(PlaywrightCliInstaller.PackageName, provenanceChecker.CapturedPackageName);
        Assert.Equal("0.1.7", provenanceChecker.CapturedVersion);
        Assert.Equal(PlaywrightCliInstaller.ExpectedSourceRepository, provenanceChecker.CapturedExpectedSourceRepository);
        Assert.Equal(PlaywrightCliInstaller.ExpectedWorkflowPath, provenanceChecker.CapturedExpectedWorkflowPath);
        Assert.Equal(PlaywrightCliInstaller.ExpectedBuildType, provenanceChecker.CapturedExpectedBuildType);
        Assert.Equal($"sha512-{Convert.ToBase64String(SHA512.HashData(npmRunner.TarballContent))}", provenanceChecker.CapturedSriIntegrity);
        Assert.False(Directory.Exists(npmRunner.PackOutputDirectory));
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenGlobalInstallFails_ReturnsErrorMessage()
    {
        var npmRunner = CreateNpmRunner();
        npmRunner.InstallGlobalResult = false;
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(npmRunner.PackOutputDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenOlderVersionInstalled_PerformsUpgrade()
    {
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner { InstalledVersion = new SemVersion(0, 1, 3) };
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public void ComputeIntegrity_ReturnsSha512SriValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "package.tgz");
        var content = "test content for hashing"u8.ToArray();
        File.WriteAllBytes(path, content);
        var expectedIntegrity = $"sha512-{Convert.ToBase64String(SHA512.HashData(content))}";

        Assert.Equal(expectedIntegrity, PlaywrightCliInstaller.ComputeIntegrity(path));
    }

    [Theory]
    [InlineData("refs/tags/0.1.7", true)]
    [InlineData("refs/tags/v0.1.7", true)]
    [InlineData("refs/tags/0.1.6", false)]
    [InlineData("refs/tags/V0.1.7", false)]
    [InlineData("refs/heads/0.1.7", false)]
    [InlineData("refs/heads/main", false)]
    public async Task InstallAsync_WorkflowRefValidator_OnlyAcceptsMatchingReleaseTags(string workflowRef, bool expected)
    {
        var provenanceChecker = new FakeNpmProvenanceChecker();
        var installer = CreateInstaller(CreateNpmRunner(), new FakePlaywrightCliRunner(), provenanceChecker);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.NotNull(provenanceChecker.CapturedValidateWorkflowRef);
        Assert.True(WorkflowRefInfo.TryParse(workflowRef, out var parsedRef));
        Assert.Equal(expected, provenanceChecker.CapturedValidateWorkflowRef(parsedRef!));
    }

    [Theory]
    [InlineData((int)ProvenanceVerificationOutcome.AttestationFetchFailed)]
    [InlineData((int)ProvenanceVerificationOutcome.AttestationParseFailed)]
    [InlineData((int)ProvenanceVerificationOutcome.SlsaProvenanceNotFound)]
    [InlineData((int)ProvenanceVerificationOutcome.PayloadDecodeFailed)]
    [InlineData((int)ProvenanceVerificationOutcome.PackageIdentityMismatch)]
    [InlineData((int)ProvenanceVerificationOutcome.PackageDigestMismatch)]
    [InlineData((int)ProvenanceVerificationOutcome.SourceRepositoryNotFound)]
    [InlineData((int)ProvenanceVerificationOutcome.SourceRepositoryMismatch)]
    [InlineData((int)ProvenanceVerificationOutcome.WorkflowMismatch)]
    [InlineData((int)ProvenanceVerificationOutcome.BuildTypeMismatch)]
    [InlineData((int)ProvenanceVerificationOutcome.WorkflowRefMismatch)]
    public async Task InstallAsync_WhenVerificationFails_DoesNotInstallOrGenerate(int outcome)
    {
        var npmRunner = CreateNpmRunner();
        var provenanceChecker = new FakeNpmProvenanceChecker { ProvenanceOutcome = (ProvenanceVerificationOutcome)outcome };
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner, provenanceChecker);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(1, provenanceChecker.CallCount);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(0, npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(npmRunner.PackOutputDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenValidationDisabled_SkipsAllValidationChecks()
    {
        var npmRunner = CreateNpmRunner();
        var provenanceChecker = new FakeNpmProvenanceChecker { ProvenanceOutcome = ProvenanceVerificationOutcome.AttestationFetchFailed };
        var playwrightRunner = new FakePlaywrightCliRunner();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.DisablePackageValidationKey] = "true"
            })
            .Build();
        var installer = CreateInstaller(npmRunner, playwrightRunner, provenanceChecker, configuration);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(0, provenanceChecker.CallCount);
        Assert.Equal(1, npmRunner.PackCallCount);
        Assert.Equal(1, npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenVersionOverrideConfigured_UsesOverrideVersion()
    {
        var npmRunner = CreateNpmRunner();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.VersionOverrideKey] = "0.2.0"
            })
            .Build();
        var installer = CreateInstaller(npmRunner, new FakePlaywrightCliRunner(), configuration: configuration);

        await installer.InstallAsync(CancellationToken.None);

        Assert.Equal("0.2.0", npmRunner.ResolvedVersionRange);
    }

    [Theory]
    [InlineData(">=0.2.0")]
    [InlineData("latest")]
    [InlineData("0.2")]
    [InlineData("not-a-version")]
    [InlineData("v0.2.0")]
    public async Task InstallAsync_WhenVersionOverrideIsNotStrictSemVer_ReturnsFailed(string invalidVersion)
    {
        var npmRunner = CreateNpmRunner();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.VersionOverrideKey] = invalidVersion
            })
            .Build();
        var installer = CreateInstaller(npmRunner, new FakePlaywrightCliRunner(), configuration: configuration);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains(invalidVersion, result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(0, npmRunner.ResolveCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenNoVersionOverride_UsesDefaultRange()
    {
        var npmRunner = CreateNpmRunner();
        var installer = CreateInstaller(npmRunner, new FakePlaywrightCliRunner());

        await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightCliInstaller.VersionRange, npmRunner.ResolvedVersionRange);
        Assert.Equal(PlaywrightCliInstaller.PackageName, npmRunner.ResolvedPackageName);
    }

    [Fact]
    public async Task InstallAsync_CapturesCompleteSkillFromIsolatedWorkspace()
    {
        var playwrightRunner = new FakePlaywrightCliRunner();
        playwrightRunner.OnInstallSkills = directory =>
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Directory.CreateDirectory(Path.Combine(directory, ".playwright"));
            Directory.CreateDirectory(Path.Combine(directory, ".github", "skills", "unselected"));
        };
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
        var content = string.Join("\n\n", result.Files.Select(static file =>
            $"{file.RelativePath.Replace('\\', '/')}\n{Encoding.UTF8.GetString(file.Content)}"));
        await Verify(content, "txt");
    }

    [Fact]
    public async Task InstallAsync_PreservesBinarySupportingFiles()
    {
        var playwrightRunner = new FakePlaywrightCliRunner();
        var relativePath = Path.Combine("assets", "example.bin");
        byte[] bytes = [0, 255, 128, 1, 13, 10];
        playwrightRunner.SkillFiles[relativePath] = bytes;
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(bytes, Assert.Single(result.Files, file => file.RelativePath == relativePath).Content);
    }

    [Fact]
    public async Task InstallAsync_WhenGenerationFails_ReturnsFailedAndCleansWorkspace()
    {
        var playwrightRunner = new FakePlaywrightCliRunner { InstallSkillsResult = false };
        playwrightRunner.OnInstallSkills = directory =>
        {
            var partialDirectory = Directory.CreateDirectory(Path.Combine(directory, ".claude", "skills"));
            File.WriteAllText(Path.Combine(partialDirectory.FullName, "partial.txt"), "partial output");
        };
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.Equal(AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles, result.Message);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_WhenGeneratedSkillIsMissingOrEmpty_ReturnsFailed(bool emptyFile)
    {
        var playwrightRunner = new FakePlaywrightCliRunner();
        playwrightRunner.SkillFiles.Clear();
        if (emptyFile)
        {
            playwrightRunner.SkillFiles["SKILL.md"] = [];
        }
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.Equal(AgentSkillInstallerStrings.PlaywrightMissingSkill, result.Message);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenGeneratedSkillContainsLink_ReturnsFailedAndPreservesLinkTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var externalPath = Path.Combine(workspace.Path, "external.md");
        await File.WriteAllTextAsync(externalPath, "user content");
        var playwrightRunner = new FakePlaywrightCliRunner();
        playwrightRunner.OnInstallSkills = directory =>
        {
            var skillDirectory = Directory.CreateDirectory(Path.Combine(
                directory, PlaywrightCliInstaller.s_primarySkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName));
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(skillDirectory.FullName, "linked.md"), externalPath, isDirectory: false);
        };
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal("user content", await File.ReadAllTextAsync(externalPath));
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenGenerationThrows_ReturnsFailedAndCleansWorkspace()
    {
        var playwrightRunner = new FakePlaywrightCliRunner
        {
            OnInstallSkills = _ => throw new IOException("generator failed")
        };
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("generator failed", result.Message);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenResolutionThrows_ReturnsFailed()
    {
        var npmRunner = CreateNpmRunner();
        npmRunner.OnResolvePackage = _ => throw new HttpRequestException("registry unavailable");
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("registry unavailable", result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(0, playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenCancelledBeforeStarting_DoesNotProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var npmRunner = CreateNpmRunner();
        var playwrightRunner = new FakePlaywrightCliRunner();
        var installer = CreateInstaller(npmRunner, playwrightRunner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cancellation.Token));

        Assert.Equal(0, npmRunner.ResolveCallCount);
        Assert.Equal(0, playwrightRunner.GetVersionCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenCancelledDuringGeneration_PropagatesAndCleansWorkspace()
    {
        using var cancellation = new CancellationTokenSource();
        var playwrightRunner = new FakePlaywrightCliRunner
        {
            OnInstallSkills = _ => cancellation.Cancel()
        };
        var installer = CreateInstaller(CreateNpmRunner(), playwrightRunner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cancellation.Token));

        Assert.False(Directory.Exists(playwrightRunner.InstallSkillsWorkingDirectory));
    }

    private static FakeNpmRunner CreateNpmRunner() => new()
    {
        ResolveResult = new NpmPackageInfo { Version = new SemVersion(0, 1, 7) }
    };

    private static PlaywrightCliInstaller CreateInstaller(
        FakeNpmRunner npmRunner,
        FakePlaywrightCliRunner playwrightRunner,
        FakeNpmProvenanceChecker? provenanceChecker = null,
        IConfiguration? configuration = null) =>
        new(
            npmRunner,
            provenanceChecker ?? new FakeNpmProvenanceChecker(),
            playwrightRunner,
            new TestInteractionService(),
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<PlaywrightCliInstaller>.Instance);
}
