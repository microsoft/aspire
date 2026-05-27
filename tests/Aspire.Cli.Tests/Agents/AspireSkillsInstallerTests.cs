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

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsInstallerTests
{
    private const string GitHubReleaseAssetBuildType = "https://actions.github.io/buildtypes/workflow/v1";

    [Fact]
    public async Task InstallAsync_WhenValidBundleIsCached_UsesCacheWithoutNetwork()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills", AspireSkillsInstaller.Version);
            await CreateCachedBundleAsync(cachedBundleDirectory);
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(embeddedBundleProvider.OpenArchiveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCachedBundleLastUsedCannotBeTouched_UsesCacheWithoutNetwork()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills", AspireSkillsInstaller.Version);
            await CreateCachedBundleAsync(cachedBundleDirectory);
            Directory.CreateDirectory(Path.Combine(cachedBundleDirectory, ".lastused"));
            var installer = CreateInstaller(executionContext);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubReleaseIsUnavailableAndNoCache_ReturnsFailure()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var installer = CreateInstaller(executionContext);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.Contains("GitHub", result.Message);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubReleaseIsUnavailableAndEmbeddedBundleMatches_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.OpenArchiveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedAspireSkillsBundleProvider_OpensSnapshotResource()
    {
        var provider = new EmbeddedAspireSkillsBundleProvider(NullLogger<EmbeddedAspireSkillsBundleProvider>.Instance);

        var metadata = Assert.IsType<EmbeddedAspireSkillsBundleMetadata>(provider.Metadata);
        using var archiveStream = Assert.IsAssignableFrom<Stream>(provider.OpenArchive());

        Assert.Equal(AspireSkillsInstaller.Version, metadata.Version);
        Assert.Equal(AspireSkillsInstaller.GitHubRepository, metadata.Repository);
        Assert.Equal(metadata.Sha256, ComputeSha256(archiveStream));
    }

    private static string ComputeSha256(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubReleaseAssetIsAvailable_UsesGitHub()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            Uri? releaseRequestUri = null;
            Uri? assetRequestUri = null;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    releaseRequestUri = request.RequestUri;
                    return CreateJsonResponse(CreateGitHubReleaseJson("aspire-skills-v0.0.1.tgz", "https://downloads.example.test/aspire-skills-v0.0.1.tgz"));
                }

                assetRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier();
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(attestationVerifier.VerifyCalled);
            Assert.Equal(AspireSkillsInstaller.GitHubRepository, attestationVerifier.Repository);
            Assert.Equal(AspireSkillsInstaller.ExpectedSourceRepository, attestationVerifier.ExpectedSourceRepository);
            Assert.Equal(AspireSkillsInstaller.ExpectedWorkflowPath, attestationVerifier.ExpectedWorkflowPath);
            Assert.Equal(GitHubReleaseAssetBuildType, attestationVerifier.ExpectedBuildType);
            Assert.Equal(AspireSkillsInstaller.Version, attestationVerifier.ExpectedVersion);
            Assert.False(embeddedBundleProvider.OpenArchiveCalled);
            Assert.NotNull(releaseRequestUri);
            Assert.NotNull(assetRequestUri);
            Assert.Contains("/microsoft/aspire-skills/releases/tags/v0.0.1", releaseRequestUri.AbsolutePath);
            Assert.Equal("https://downloads.example.test/aspire-skills-v0.0.1.tgz", assetRequestUri.AbsoluteUri);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubAttestationFails_FallsBackToEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson("aspire-skills-v0.0.1.tgz", "https://downloads.example.test/aspire-skills-v0.0.1.tgz"));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier
            {
                Result = new ProvenanceVerificationResult { Outcome = ProvenanceVerificationOutcome.WorkflowMismatch }
            };
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(attestationVerifier.VerifyCalled);
            Assert.True(embeddedBundleProvider.OpenArchiveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenVersionOverrideDoesNotMatchEmbeddedBundle_DoesNotUseEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.VersionOverrideKey] = "9.9.9"
                })
                .Build();
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.False(embeddedBundleProvider.OpenArchiveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenEmbeddedArchiveHashDoesNotMatch_ReturnsFailure()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            embeddedBundleProvider.Metadata = new EmbeddedAspireSkillsBundleMetadata
            {
                Version = AspireSkillsInstaller.Version,
                Repository = AspireSkillsInstaller.GitHubRepository,
                Tag = $"v{AspireSkillsInstaller.Version}",
                AssetName = $"aspire-skills-v{AspireSkillsInstaller.Version}.tgz",
                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            };
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("SHA-256", result.Message, StringComparison.Ordinal);
            Assert.Contains("0000000000000000000000000000000000000000000000000000000000000000", result.Message, StringComparison.Ordinal);
            Assert.True(embeddedBundleProvider.OpenArchiveCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static AspireSkillsInstaller CreateInstaller(
        CliExecutionContext executionContext,
        HttpMessageHandler? httpMessageHandler = null,
        TestGitHubArtifactAttestationVerifier? githubArtifactAttestationVerifier = null,
        IConfiguration? configuration = null,
        IEmbeddedAspireSkillsBundleProvider? embeddedBundleProvider = null)
    {
        return new AspireSkillsInstaller(
            githubArtifactAttestationVerifier ?? new TestGitHubArtifactAttestationVerifier(),
            new MockHttpClientFactory(httpMessageHandler ?? new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            embeddedBundleProvider ?? new TestEmbeddedAspireSkillsBundleProvider(),
            new TestInteractionService(),
            executionContext,
            configuration ?? new ConfigurationBuilder().Build(),
            TestTelemetryHelper.CreateInitializedTelemetry(),
            NullLogger<AspireSkillsInstaller>.Instance);
    }

    private static async Task CreateCachedBundleAsync(string bundleDirectory)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name);
        Directory.CreateDirectory(skillDirectory);

        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath,
            """
            ---
            name: aspire
            description: "Aspire CLI commands and workflows for distributed apps"
            ---

            # Aspire
            """);

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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<TestEmbeddedAspireSkillsBundleProvider> CreateEmbeddedBundleProviderAsync()
    {
        var archiveBytes = await CreateBundleArchiveBytesAsync();
        return new TestEmbeddedAspireSkillsBundleProvider
        {
            Metadata = new EmbeddedAspireSkillsBundleMetadata
            {
                Version = AspireSkillsInstaller.Version,
                Repository = AspireSkillsInstaller.GitHubRepository,
                Tag = $"v{AspireSkillsInstaller.Version}",
                AssetName = $"aspire-skills-v{AspireSkillsInstaller.Version}.tgz",
                Sha256 = ComputeSha256(archiveBytes)
            },
            ArchiveBytes = archiveBytes
        };
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private static string CreateGitHubReleaseJson(string assetName, string downloadUrl)
    {
        return $$"""
            {
              "tag_name": "v{{AspireSkillsInstaller.Version}}",
              "assets": [
                {
                  "name": "{{assetName}}",
                  "browser_download_url": "{{downloadUrl}}"
                }
              ]
            }
            """;
    }

    private static string CreateTempDirectory()
    {
        return Directory.CreateTempSubdirectory("aspire-skills-installer-test-").FullName;
    }

    private sealed class TestGitHubArtifactAttestationVerifier : IGitHubArtifactAttestationVerifier
    {
        public bool VerifyCalled { get; private set; }

        public string? Repository { get; private set; }

        public string? ExpectedSourceRepository { get; private set; }

        public string? ExpectedWorkflowPath { get; private set; }

        public string? ExpectedBuildType { get; private set; }

        public string? ExpectedVersion { get; private set; }

        public ProvenanceVerificationResult Result { get; init; } = new()
        {
            Outcome = ProvenanceVerificationOutcome.Verified,
            Provenance = new NpmProvenanceData { SourceRepository = AspireSkillsInstaller.ExpectedSourceRepository }
        };

        public Task<ProvenanceVerificationResult> VerifyAsync(
            string repository,
            string artifactPath,
            string expectedSourceRepository,
            string expectedWorkflowPath,
            string expectedBuildType,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            VerifyCalled = true;
            Repository = repository;
            ExpectedSourceRepository = expectedSourceRepository;
            ExpectedWorkflowPath = expectedWorkflowPath;
            ExpectedBuildType = expectedBuildType;
            ExpectedVersion = expectedVersion;

            return Task.FromResult(Result);
        }
    }

    private sealed class TestEmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
    {
        public EmbeddedAspireSkillsBundleMetadata? Metadata { get; set; }

        public byte[]? ArchiveBytes { get; init; }

        public bool OpenArchiveCalled { get; private set; }

        public Stream? OpenArchive()
        {
            OpenArchiveCalled = true;
            return ArchiveBytes is null ? null : new MemoryStream(ArchiveBytes, writable: false);
        }
    }
}
