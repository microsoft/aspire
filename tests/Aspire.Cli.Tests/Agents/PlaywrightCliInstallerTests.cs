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
    private readonly FakeNpmRunner _npmRunner = new() { ResolveResult = new NpmPackageInfo { Version = new SemVersion(0, 1, 7) } };
    private readonly FakePlaywrightCliRunner _playwrightRunner = new();
    private readonly FakeNpmProvenanceChecker _provenanceChecker = new();

    [Theory]
    [InlineData("npm", 0, 0, 0, 0, 0, 0)]
    [InlineData("resolve", 1, 0, 0, 0, 0, 0)]
    [InlineData("pack", 1, 1, 1, 0, 0, 0)]
    [InlineData("install", 1, 1, 1, 1, 1, 0)]
    [InlineData("generate", 1, 1, 1, 1, 1, 1)]
    [InlineData("generate-error", 1, 1, 1, 1, 1, 1)]
    [InlineData("resolve-error", 1, 0, 0, 0, 0, 0)]
    public async Task InstallAsync_FailureStopsLaterStagesAndCleansOwnedDirectories(
        string failure, int resolves, int probes, int packs, int verifications, int installs, int generations)
    {
        _playwrightRunner.OnInstallSkills = directory =>
        {
            File.WriteAllText(Path.Combine(directory, "partial.txt"), "partial output");
            if (failure == "generate-error")
            {
                throw new IOException("generator failed");
            }
        };
        switch (failure)
        {
            case "npm":
                _npmRunner.IsAvailable = false;
                break;
            case "resolve":
                _npmRunner.ResolveResult = null;
                break;
            case "pack":
                _npmRunner.PackResult = null;
                break;
            case "install":
                _npmRunner.InstallGlobalResult = false;
                break;
            case "generate":
                _playwrightRunner.InstallSkillsResult = false;
                break;
            case "resolve-error":
                _npmRunner.OnResolvePackage = _ => throw new HttpRequestException("registry unavailable");
                break;
        }

        var result = await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(failure == "npm" ? PlaywrightInstallStatus.Skipped : PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotEmpty(result.Message!);
        Assert.Empty(result.Files);
        Assert.Equal(resolves, _npmRunner.ResolveCallCount);
        Assert.Equal(probes, _playwrightRunner.GetVersionCallCount);
        Assert.Equal(packs, _npmRunner.PackCallCount);
        Assert.Equal(verifications, _provenanceChecker.CallCount);
        Assert.Equal(installs, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(generations, _playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(_npmRunner.PackOutputDirectory));
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
        if (failure == "generate-error")
        {
            Assert.Contains("generator failed", result.Message);
        }
        if (failure == "resolve-error")
        {
            Assert.Contains("registry unavailable", result.Message);
        }
        if (failure is "npm" or "generate")
        {
            Assert.Equal(failure == "npm" ? AgentCommandStrings.InitCommand_PlaywrightCliSkipped :
                AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles, result.Message);
        }
    }

    [Theory]
    [InlineData("0.1.7")]
    [InlineData("0.2.0")]
    public async Task InstallAsync_WhenSuitableVersionIsInstalled_SkipsInstallAndGeneratesSkills(string installedVersion)
    {
        _playwrightRunner.InstalledVersion = SemVersion.Parse(installedVersion, SemVersionStyles.Strict);
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, _npmRunner.ResolveCallCount);
        Assert.Equal(1, _playwrightRunner.GetVersionCallCount);
        Assert.Equal(1, _playwrightRunner.InstallSkillsCallCount);
        Assert.Equal(0, _npmRunner.PackCallCount);
        Assert.Equal(0, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, _provenanceChecker.CallCount);
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_VerifiesDownloadedTarballBeforeInstallingGlobally()
    {
        _npmRunner.TarballContent = [10, 20, 30, 40, 50];
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, _npmRunner.PackCallCount);
        Assert.Equal(1, _provenanceChecker.CallCount);
        Assert.Equal(1, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, _playwrightRunner.InstallSkillsCallCount);
        Assert.Equal(_npmRunner.PackedTarballPath, _npmRunner.InstalledTarballPath);
        Assert.Equal(PlaywrightCliInstaller.PackageName, _provenanceChecker.CapturedPackageName);
        Assert.Equal("0.1.7", _provenanceChecker.CapturedVersion);
        Assert.Equal(PlaywrightCliInstaller.ExpectedSourceRepository, _provenanceChecker.CapturedExpectedSourceRepository);
        Assert.Equal(PlaywrightCliInstaller.ExpectedWorkflowPath, _provenanceChecker.CapturedExpectedWorkflowPath);
        Assert.Equal(PlaywrightCliInstaller.ExpectedBuildType, _provenanceChecker.CapturedExpectedBuildType);
        Assert.Equal($"sha512-{Convert.ToBase64String(SHA512.HashData(_npmRunner.TarballContent))}", _provenanceChecker.CapturedSriIntegrity);
        Assert.False(Directory.Exists(_npmRunner.PackOutputDirectory));
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenOlderVersionInstalled_PerformsUpgrade()
    {
        _playwrightRunner.InstalledVersion = new SemVersion(0, 1, 3);
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, _npmRunner.PackCallCount);
        Assert.Equal(1, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, _playwrightRunner.InstallSkillsCallCount);
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
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.NotNull(_provenanceChecker.CapturedValidateWorkflowRef);
        Assert.True(WorkflowRefInfo.TryParse(workflowRef, out var parsedRef));
        Assert.Equal(expected, _provenanceChecker.CapturedValidateWorkflowRef(parsedRef!));
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
        _provenanceChecker.ProvenanceOutcome = (ProvenanceVerificationOutcome)outcome;
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(1, _provenanceChecker.CallCount);
        Assert.Equal(1, _npmRunner.PackCallCount);
        Assert.Equal(0, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(0, _playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(_npmRunner.PackOutputDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenValidationDisabled_SkipsAllValidationChecks()
    {
        _provenanceChecker.ProvenanceOutcome = ProvenanceVerificationOutcome.AttestationFetchFailed;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.DisablePackageValidationKey] = "true"
            })
            .Build();
        var installer = CreateInstaller(configuration);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(0, _provenanceChecker.CallCount);
        Assert.Equal(1, _npmRunner.PackCallCount);
        Assert.Equal(1, _npmRunner.InstallGlobalCallCount);
        Assert.Equal(1, _playwrightRunner.InstallSkillsCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenVersionOverrideConfigured_UsesOverrideVersion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.VersionOverrideKey] = "0.2.0"
            })
            .Build();
        var installer = CreateInstaller(configuration);

        await installer.InstallAsync(CancellationToken.None);

        Assert.Equal("0.2.0", _npmRunner.ResolvedVersionRange);
    }

    [Theory]
    [InlineData(">=0.2.0")]
    [InlineData("latest")]
    [InlineData("0.2")]
    [InlineData("not-a-version")]
    [InlineData("v0.2.0")]
    public async Task InstallAsync_WhenVersionOverrideIsNotStrictSemVer_ReturnsFailed(string invalidVersion)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlaywrightCliInstaller.VersionOverrideKey] = invalidVersion
            })
            .Build();
        var installer = CreateInstaller(configuration);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains(invalidVersion, result.Message);
        Assert.Empty(result.Files);
        Assert.Equal(0, _npmRunner.ResolveCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenNoVersionOverride_UsesDefaultRange()
    {
        var installer = CreateInstaller();

        await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightCliInstaller.VersionRange, _npmRunner.ResolvedVersionRange);
        Assert.Equal(PlaywrightCliInstaller.PackageName, _npmRunner.ResolvedPackageName);
    }

    [Fact]
    public async Task InstallAsync_CapturesCompleteSkillFromIsolatedWorkspace()
    {
        _playwrightRunner.OnInstallSkills = directory =>
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Directory.CreateDirectory(Path.Combine(directory, ".playwright"));
            Directory.CreateDirectory(Path.Combine(directory, ".github", "skills", "unselected"));
        };
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(1, _playwrightRunner.InstallSkillsCallCount);
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
        var content = string.Join("\n\n", result.Files.Select(static file =>
            $"{file.RelativePath.Replace('\\', '/')}\n{Encoding.UTF8.GetString(file.Content)}"));
        await Verify(content, "txt");
    }

    [Fact]
    public async Task InstallAsync_PreservesBinarySupportingFiles()
    {
        var relativePath = Path.Combine("assets", "example.bin");
        byte[] bytes = [0, 255, 128, 1, 13, 10];
        _playwrightRunner.SkillFiles[relativePath] = bytes;
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Installed, result.Status);
        Assert.Equal(bytes, Assert.Single(result.Files, file => file.RelativePath == relativePath).Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_WhenGeneratedSkillIsMissingOrEmpty_ReturnsFailed(bool emptyFile)
    {
        _playwrightRunner.SkillFiles.Clear();
        if (emptyFile)
        {
            _playwrightRunner.SkillFiles["SKILL.md"] = [];
        }
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.Equal(AgentCommandStrings.PlaywrightCliInstaller_FailedToGenerateSkillFiles, result.Message);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenGeneratedSkillContainsLink_ReturnsFailedAndPreservesLinkTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var externalPath = Path.Combine(workspace.Path, "external.md");
        await File.WriteAllTextAsync(externalPath, "user content");
        _playwrightRunner.OnInstallSkills = directory =>
        {
            var skillDirectory = Directory.CreateDirectory(Path.Combine(
                directory, PlaywrightCliInstaller.s_primarySkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName));
            TestSymlinkHelper.TryCreateSymlink(Path.Combine(skillDirectory.FullName, "linked.md"), externalPath, isDirectory: false);
        };
        var installer = CreateInstaller();

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Equal(PlaywrightInstallStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(result.Files);
        Assert.Equal("user content", await File.ReadAllTextAsync(externalPath));
        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
    }

    [Fact]
    public async Task InstallAsync_WhenCancelledBeforeStarting_DoesNotProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var installer = CreateInstaller();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cancellation.Token));

        Assert.Equal(0, _npmRunner.ResolveCallCount);
        Assert.Equal(0, _playwrightRunner.GetVersionCallCount);
    }

    [Fact]
    public async Task InstallAsync_WhenCancelledDuringGeneration_PropagatesAndCleansWorkspace()
    {
        using var cancellation = new CancellationTokenSource();
        _playwrightRunner.OnInstallSkills = _ => cancellation.Cancel();
        var installer = CreateInstaller();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(cancellation.Token));

        Assert.False(Directory.Exists(_playwrightRunner.InstallSkillsWorkingDirectory));
    }

    private PlaywrightCliInstaller CreateInstaller(IConfiguration? configuration = null)
        => new(_npmRunner, _provenanceChecker, _playwrightRunner, new TestInteractionService(),
            configuration ?? new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
}
