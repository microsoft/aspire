// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsInstallerTests
{
    [Fact]
    public async Task InstallAsync_WhenValidBundleIsCached_UsesCacheWithoutNpm()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills", AspireSkillsInstaller.Version);
            await CreateCachedBundleAsync(cachedBundleDirectory);
            var npmRunner = new TestNpmRunner { IsAvailable = false };
            var installer = CreateInstaller(npmRunner, executionContext);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(npmRunner.ResolveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenNpmIsUnavailableAndNoCache_ReturnsFailure()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var npmRunner = new TestNpmRunner { IsAvailable = false };
            var installer = CreateInstaller(npmRunner, executionContext);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.Contains("npm", result.Message);
            Assert.False(npmRunner.ResolveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static AspireSkillsInstaller CreateInstaller(TestNpmRunner npmRunner, CliExecutionContext executionContext)
    {
        return new AspireSkillsInstaller(
            npmRunner,
            new TestNpmProvenanceChecker(),
            new TestInteractionService(),
            executionContext,
            new ConfigurationBuilder().Build(),
            TestTelemetryHelper.CreateInitializedTelemetry(),
            NullLogger<AspireSkillsInstaller>.Instance);
    }

    private static async Task CreateCachedBundleAsync(string bundleDirectory)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name);
        Directory.CreateDirectory(skillDirectory);

        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, "# Aspire");

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Skills =
            [
                new SkillBundleSkill
                {
                    Name = SkillDefinition.Aspire.Name,
                    Description = SkillDefinition.Aspire.Description,
                    IsDefault = true,
                    Files =
                    [
                        new SkillBundleFile
                        {
                            RelativePath = "SKILL.md",
                            Sha256 = ComputeSha256(skillPath)
                        }
                    ]
                }
            ]
        };

        var manifestJson = JsonSerializer.Serialize(manifest, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest);
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aspire-skills-installer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class TestNpmRunner : INpmRunner
    {
        public bool IsAvailable { get; init; } = true;

        public bool ResolveCalled { get; private set; }

        public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
        {
            ResolveCalled = true;
            return Task.FromResult<NpmPackageInfo?>(null);
        }

        public Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class TestNpmProvenanceChecker : INpmProvenanceChecker
    {
        public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(
            string packageName,
            string version,
            string expectedSourceRepository,
            string expectedWorkflowPath,
            string expectedBuildType,
            Func<WorkflowRefInfo, bool>? validateWorkflowRef,
            CancellationToken cancellationToken,
            string? sriIntegrity = null)
        {
            return Task.FromResult(new ProvenanceVerificationResult
            {
                Outcome = ProvenanceVerificationOutcome.Verified,
                Provenance = new NpmProvenanceData { SourceRepository = expectedSourceRepository }
            });
        }
    }
}
