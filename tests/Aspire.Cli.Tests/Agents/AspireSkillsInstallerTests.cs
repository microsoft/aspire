// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
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
using Semver;

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

    [Fact]
    public async Task InstallAsync_WhenGitHubArchiveIsAvailable_UsesGitHubWithoutNpm()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            Uri? requestUri = null;
            var handler = new MockHttpMessageHandler(request =>
            {
                requestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var npmRunner = new TestNpmRunner { IsAvailable = false };
            var installer = CreateInstaller(npmRunner, executionContext, handler);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(npmRunner.ResolveCalled);
            Assert.NotNull(requestUri);
            Assert.Contains("/microsoft/aspire-skills/archive/refs/tags/v0.0.1.tar.gz", requestUri.AbsoluteUri);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubArchiveIsUnavailable_FallsBackToNpm()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var tarballPath = await CreateBundleArchiveFileAsync(rootDirectory);
            var npmRunner = new TestNpmRunner
            {
                PackageInfo = new NpmPackageInfo
                {
                    Version = SemVersion.Parse(AspireSkillsInstaller.Version),
                    Integrity = ComputeSriSha512(tarballPath)
                },
                TarballPath = tarballPath
            };
            var installer = CreateInstaller(npmRunner, executionContext);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(npmRunner.ResolveCalled);
            Assert.True(npmRunner.PackCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubArchiveIsInvalid_DoesNotFallbackToNpm()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("not a gzip archive"u8.ToArray())
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var npmRunner = new TestNpmRunner
            {
                PackageInfo = new NpmPackageInfo
                {
                    Version = SemVersion.Parse(AspireSkillsInstaller.Version),
                    Integrity = "sha512-invalid"
                }
            };
            var installer = CreateInstaller(npmRunner, executionContext, handler);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("invalid", result.Message, StringComparison.Ordinal);
            Assert.False(npmRunner.ResolveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static AspireSkillsInstaller CreateInstaller(
        TestNpmRunner npmRunner,
        CliExecutionContext executionContext,
        HttpMessageHandler? httpMessageHandler = null)
    {
        return new AspireSkillsInstaller(
            npmRunner,
            new TestNpmProvenanceChecker(),
            new MockHttpClientFactory(httpMessageHandler ?? new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound))),
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
            Supports = CreateSupports(),
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

    private static SkillBundleSupports CreateSupports()
    {
        return new SkillBundleSupports
        {
            AspireCli = ">=0.0.0 <999.0.0",
            AspireSdk = ">=0.0.0 <999.0.0"
        };
    }

    private static async Task<byte[]> CreateBundleArchiveBytesAsync()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var bundleDirectory = Path.Combine(rootDirectory, $"aspire-skills-v{AspireSkillsInstaller.Version}");
            await CreateCachedBundleAsync(bundleDirectory);

            await using var archiveStream = new MemoryStream();
            await using (var gzipStream = new GZipStream(archiveStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                TarFile.CreateFromDirectory(bundleDirectory, gzipStream, includeBaseDirectory: true);
            }

            return archiveStream.ToArray();
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task<string> CreateBundleArchiveFileAsync(string outputDirectory)
    {
        var archivePath = Path.Combine(outputDirectory, "aspire-skills.tgz");
        await File.WriteAllBytesAsync(archivePath, await CreateBundleArchiveBytesAsync());
        return archivePath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSriSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return $"sha512-{Convert.ToBase64String(SHA512.HashData(stream))}";
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

        public bool PackCalled { get; private set; }

        public NpmPackageInfo? PackageInfo { get; init; }

        public string? TarballPath { get; init; }

        public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
        {
            ResolveCalled = true;
            return Task.FromResult(PackageInfo);
        }

        public Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
        {
            PackCalled = true;
            return Task.FromResult(TarballPath);
        }

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
