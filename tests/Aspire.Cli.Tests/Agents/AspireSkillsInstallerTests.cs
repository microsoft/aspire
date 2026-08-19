// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Configuration;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsInstallerTests
{
    private const string AspireSkillDescription = "Aspire CLI commands and workflows for distributed apps";
    private const string CacheLockRetryLogMessage = "Acquiring the Aspire skills cache lock";
    private const string GitHubReleaseAssetBuildType = "https://actions.github.io/buildtypes/workflow/v1";

    [Fact]
    public async Task InstallAsync_WhenValidBundleIsCached_UsesCacheWithoutNetwork()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(cachedBundleDirectory, archiveSha512: embeddedBundleProvider.Metadata!.Sha512);
            var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called when remote fetch is disabled."));
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(Directory.Exists(cachedBundleDirectory));
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true, true)]
    [InlineData(unchecked((int)0x80070021), true, true)]
    [InlineData(unchecked((int)0x80070005), true, false)]
    [InlineData(unchecked((int)0x80070070), true, false)]
    [InlineData(11, false, true)]
    [InlineData(35, false, true)]
    [InlineData(2, false, false)]
    [InlineData(28, false, false)]
    public void IsCacheLockContention_OnlyMatchesPlatformLockErrors(int hresult, bool isWindows, bool expected)
    {
        var exception = new IOException("Cache lock failed.", hresult);

        Assert.Equal(expected, AspireSkillsInstaller.IsCacheLockContention(exception, isWindows));
    }

    [Fact]
    public async Task InstallAsync_WhenRequiredCacheLockExceedsCleanupRetryBudget_WaitsForRelease()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            Directory.CreateDirectory(cacheRoot);
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var cleanupRetryBudgetExceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryCount = 0;
            var sink = new TestSink();
            sink.MessageLogged += context =>
            {
                if (context.Message?.Contains(CacheLockRetryLogMessage, StringComparison.Ordinal) == true &&
                    Interlocked.Increment(ref retryCount) == 4)
                {
                    cleanupRetryBudgetExceeded.TrySetResult();
                }
            };
            var logger = new TestLogger<AspireSkillsInstaller>(new TestLoggerFactory(sink, enabled: true));
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features,
                logger: logger);

            var cacheLockPath = Path.Combine(cacheRoot, $".{AspireSkillsInstaller.Version}.lock");
            await using var cacheLock = new FileStream(
                cacheLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);

            var installTask = installer.InstallAsync(CancellationToken.None);
            var completedTask = await Task.WhenAny(cleanupRetryBudgetExceeded.Task, installTask).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(cleanupRetryBudgetExceeded.Task, completedTask);
            Assert.False(installTask.IsCompleted);

            await cacheLock.DisposeAsync();
            var result = await installTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubDigestMatchesCachedBundle_UsesCacheWithoutDownloadingAsset()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            await CreateCachedBundleAsync(
                cachedBundleDirectory,
                archiveSha512: archiveSha512,
                githubArchiveSha256: archiveSha256,
                githubAttestationVerified: true);
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                assetDownloadRequested = true;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(assetDownloadRequested);
            Assert.False(attestationVerifier.VerifyCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubDigestMatchesUnverifiedCache_DownloadsAttestsAndReplacesCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync(skillBody: "# Downloaded GitHub");
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            await CreateCachedBundleAsync(
                cachedBundleDirectory,
                archiveSha512: archiveSha512,
                githubArchiveSha256: archiveSha256,
                skillBody: "# Unverified Cache");
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                assetDownloadRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(assetDownloadRequested);
            Assert.True(attestationVerifier.VerifyCalled);
            Assert.True(File.Exists(Path.Combine(
                cachedBundleDirectory,
                AspireSkillsInstaller.GitHubAttestationVerifiedFileName)));
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Downloaded GitHub", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenUnverifiedArchiveContainsAttestationMarker_DoesNotTrustCachedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync(includeGitHubAttestationMarker: true);
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.DisablePackageValidationKey] = "true"
                })
                .Build();
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                configuration: configuration);

            var firstResult = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, firstResult.Status);
            Assert.False(File.Exists(Path.Combine(
                GetBundleCacheDirectory(executionContext, archiveSha512),
                AspireSkillsInstaller.GitHubAttestationVerifiedFileName)));

            var secondInstaller = CreateInstaller(executionContext);
            var secondResult = await secondInstaller.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, secondResult.Status);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubDigestChangesForCachedVersion_DownloadsAndKeepsBothCaches()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync(skillBody: "# Downloaded GitHub");
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var staleArchiveSha256 = new string('0', 64);
            var staleArchiveSha512 = new string('0', 128);
            var staleCachedBundleDirectory = GetBundleCacheDirectory(executionContext, staleArchiveSha512);
            await CreateCachedBundleAsync(
                staleCachedBundleDirectory,
                archiveSha512: staleArchiveSha512,
                githubArchiveSha256: staleArchiveSha256,
                githubAttestationVerified: true,
                skillBody: "# Stale GitHub");
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                assetDownloadRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var installer = CreateInstaller(executionContext, httpMessageHandler: handler);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(assetDownloadRequested);
            Assert.True(Directory.Exists(staleCachedBundleDirectory));
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            Assert.Equal(
                archiveSha512,
                await File.ReadAllTextAsync(Path.Combine(
                    cachedBundleDirectory,
                    AspireSkillsInstaller.ArchiveSha512FileName)));
            Assert.Equal(
                archiveSha256,
                await File.ReadAllTextAsync(Path.Combine(
                    cachedBundleDirectory,
                    AspireSkillsInstaller.GitHubArchiveSha256FileName)));
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Downloaded GitHub", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenEmbeddedArchiveHashChangesForCachedVersion_KeepsBothCaches()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var oldEmbeddedBundleProvider = await CreateEmbeddedBundleProviderAsync(new SkillBundleSupports
            {
                AspireCli = ">=0.0.1 <0.0.2",
                AspireSdk = ">=0.0.1 <0.0.2"
            });
            var oldInstaller = CreateInstaller(
                executionContext,
                embeddedBundleProvider: oldEmbeddedBundleProvider,
                features: features);

            var oldResult = await oldInstaller.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, oldResult.Status);
            Assert.NotNull(oldResult.Bundle);
            var oldCachedBundleDirectory = GetBundleCacheDirectory(
                executionContext,
                oldEmbeddedBundleProvider.Metadata!.Sha512!);
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            Assert.NotEqual(oldEmbeddedBundleProvider.Metadata!.Sha512, embeddedBundleProvider.Metadata!.Sha512);
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            Assert.True(Directory.Exists(oldCachedBundleDirectory));
            var cachedBundleDirectory = GetBundleCacheDirectory(
                executionContext,
                embeddedBundleProvider.Metadata.Sha512!);
            Assert.Equal(
                embeddedBundleProvider.Metadata.Sha512,
                await File.ReadAllTextAsync(Path.Combine(
                    cachedBundleDirectory,
                    AspireSkillsInstaller.ArchiveSha512FileName)));
            Assert.NotEmpty(await oldResult.Bundle.GetSkillFilesAsync(
                oldResult.Bundle.GetSkillDefinitions()[0],
                CancellationToken.None));

            var restoredEmbeddedBundleProvider = new TestEmbeddedAspireSkillsBundleProvider
            {
                Metadata = oldEmbeddedBundleProvider.Metadata,
                ArchiveBytes = oldEmbeddedBundleProvider.ArchiveBytes
            };
            var restoredInstaller = CreateInstaller(
                executionContext,
                embeddedBundleProvider: restoredEmbeddedBundleProvider,
                features: features);

            var restoredResult = await restoredInstaller.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, restoredResult.Status);
            Assert.False(restoredEmbeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCachedBundleDoesNotHaveArchiveHash_ReplacesLegacyCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills", AspireSkillsInstaller.Version);
            await CreateCachedBundleAsync(cachedBundleDirectory);
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            Assert.False(File.Exists(Path.Combine(cachedBundleDirectory, "skill-manifest.json")));
            Assert.False(Directory.Exists(Path.Combine(cachedBundleDirectory, "skills")));
            var bundleCacheDirectory = GetBundleCacheDirectory(
                executionContext,
                embeddedBundleProvider.Metadata!.Sha512!);
            Assert.Equal(
                embeddedBundleProvider.Metadata.Sha512,
                await File.ReadAllTextAsync(Path.Combine(
                    bundleCacheDirectory,
                    AspireSkillsInstaller.ArchiveSha512FileName)));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCachedManifestIsMalformed_ReplacesCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(
                cachedBundleDirectory,
                archiveSha512: embeddedBundleProvider.Metadata!.Sha512);
            await File.WriteAllTextAsync(Path.Combine(cachedBundleDirectory, "skill-manifest.json"), "{");
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
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
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(cachedBundleDirectory, archiveSha512: embeddedBundleProvider.Metadata!.Sha512);
            Directory.CreateDirectory(Path.Combine(cachedBundleDirectory, ".lastused"));
            var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called when remote fetch is disabled."));
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(Directory.Exists(cachedBundleDirectory));
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenStaleCacheLastUsedIsOutOfRange_IgnoresInvalidMarker()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(cachedBundleDirectory, archiveSha512: embeddedBundleProvider.Metadata!.Sha512);
            var staleArchiveSha512 = new string('a', 128);
            var staleCacheDirectory = Path.Combine(
                executionContext.CacheDirectory.FullName,
                "aspire-skills",
                "9.9.9",
                staleArchiveSha512);
            await CreateCachedBundleAsync(staleCacheDirectory, archiveSha512: staleArchiveSha512);
            await File.WriteAllTextAsync(
                Path.Combine(staleCacheDirectory, ".lastused"),
                long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called when remote fetch is disabled."));
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
            Assert.True(Directory.Exists(staleCacheDirectory));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_RemovesStaleLegacyCacheForOtherVersion()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            var staleVersionDirectory = Path.Combine(cacheRoot, "9.9.9");
            await CreateCachedBundleAsync(staleVersionDirectory);
            await WriteLastUsedAsync(staleVersionDirectory, DateTimeOffset.UtcNow.AddDays(-1));

            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var currentCacheDirectory = GetBundleCacheDirectory(
                executionContext,
                embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(
                currentCacheDirectory,
                archiveSha512: embeddedBundleProvider.Metadata.Sha512);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.MaxCacheAgeKey] = "60"
                })
                .Build();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.False(Directory.Exists(staleVersionDirectory));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_RemovesEmptyVersionDirectoryAfterStaleDigestCleanup()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            var staleVersionDirectory = Path.Combine(cacheRoot, "9.9.9");
            var staleArchiveSha512 = new string('a', 128);
            var staleCacheDirectory = Path.Combine(staleVersionDirectory, staleArchiveSha512);
            await CreateCachedBundleAsync(staleCacheDirectory, archiveSha512: staleArchiveSha512);
            await WriteLastUsedAsync(staleCacheDirectory, DateTimeOffset.UtcNow.AddDays(-1));

            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var currentCacheDirectory = GetBundleCacheDirectory(
                executionContext,
                embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(
                currentCacheDirectory,
                archiveSha512: embeddedBundleProvider.Metadata.Sha512);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.MaxCacheAgeKey] = "60"
                })
                .Build();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.False(Directory.Exists(staleVersionDirectory));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenStaleVersionLockIsReleasedDuringBackoff_RechecksLastUsed()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            Directory.CreateDirectory(cacheRoot);
            const string staleVersion = "9.9.9";
            var staleArchiveSha512 = new string('a', 128);
            var staleCacheDirectory = Path.Combine(cacheRoot, staleVersion, staleArchiveSha512);
            await CreateCachedBundleAsync(staleCacheDirectory, archiveSha512: staleArchiveSha512);
            await WriteLastUsedAsync(staleCacheDirectory, DateTimeOffset.UtcNow.AddDays(-1));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.MaxCacheAgeKey] = "60"
                })
                .Build();
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var retryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new TestSink();
            sink.MessageLogged += context =>
            {
                if (context.Message?.Contains(CacheLockRetryLogMessage, StringComparison.Ordinal) == true)
                {
                    retryObserved.TrySetResult();
                }
            };
            var logger = new TestLogger<AspireSkillsInstaller>(new TestLoggerFactory(sink, enabled: true));
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features,
                logger: logger);

            var staleVersionLockPath = Path.Combine(cacheRoot, $".{staleVersion}.lock");
            await using var staleVersionLock = new FileStream(
                staleVersionLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);

            var installTask = installer.InstallAsync(CancellationToken.None);
            await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WriteLastUsedAsync(staleCacheDirectory, DateTimeOffset.UtcNow);
            await staleVersionLock.DisposeAsync();
            var result = await installTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.True(Directory.Exists(staleCacheDirectory));
            Assert.Contains(sink.Writes, context =>
                context.Message?.Contains(CacheLockRetryLogMessage, StringComparison.Ordinal) == true);
            Assert.Equal(0, sink.Writes.Count(context =>
                context.Message?.Contains("Skipping cleanup", StringComparison.Ordinal) == true));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenStaleVersionLockExceedsRetryBudget_SkipsLockedVersion()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            Directory.CreateDirectory(cacheRoot);
            const string staleVersion = "9.9.9";
            var staleArchiveSha512 = new string('a', 128);
            var staleCacheDirectory = Path.Combine(cacheRoot, staleVersion, staleArchiveSha512);
            await CreateCachedBundleAsync(staleCacheDirectory, archiveSha512: staleArchiveSha512);
            await WriteLastUsedAsync(staleCacheDirectory, DateTimeOffset.UtcNow.AddDays(-1));

            var staleVersionLockPath = Path.Combine(cacheRoot, $".{staleVersion}.lock");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.MaxCacheAgeKey] = "60"
                })
                .Build();
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var sink = new TestSink();
            var logger = new TestLogger<AspireSkillsInstaller>(new TestLoggerFactory(sink, enabled: true));
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features,
                logger: logger);

            await using (var staleVersionLock = new FileStream(
                staleVersionLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                var result = await installer.InstallAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
                Assert.True(Directory.Exists(staleCacheDirectory));
                Assert.Equal(3, sink.Writes.Count(context =>
                    context.Message?.Contains(CacheLockRetryLogMessage, StringComparison.Ordinal) == true));
                Assert.Equal(1, sink.Writes.Count(context =>
                    context.Message?.Contains("Skipping cleanup", StringComparison.Ordinal) == true));
            }

            var cleanupResult = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, cleanupResult.Status);
            Assert.False(Directory.Exists(staleCacheDirectory));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_RemovesOnlyStaleDigestCachesForCurrentVersion()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var currentCacheDirectory = GetBundleCacheDirectory(
                executionContext,
                embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(
                currentCacheDirectory,
                archiveSha512: embeddedBundleProvider.Metadata.Sha512);

            var staleArchiveSha512 = new string('a', 128);
            var staleCacheDirectory = GetBundleCacheDirectory(executionContext, staleArchiveSha512);
            await CreateCachedBundleAsync(staleCacheDirectory, archiveSha512: staleArchiveSha512);
            await WriteLastUsedAsync(staleCacheDirectory, DateTimeOffset.UtcNow.AddDays(-1));

            var recentArchiveSha512 = new string('b', 128);
            var recentCacheDirectory = GetBundleCacheDirectory(executionContext, recentArchiveSha512);
            await CreateCachedBundleAsync(recentCacheDirectory, archiveSha512: recentArchiveSha512);
            await WriteLastUsedAsync(recentCacheDirectory, DateTimeOffset.UtcNow);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AspireSkillsInstaller.MaxCacheAgeKey] = "60"
                })
                .Build();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                configuration: configuration,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.True(Directory.Exists(currentCacheDirectory));
            Assert.False(Directory.Exists(staleCacheDirectory));
            Assert.True(Directory.Exists(recentCacheDirectory));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_LeavesVersionLockFileAsStableSynchronizationAnchor()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.True(File.Exists(Path.Combine(
                executionContext.CacheDirectory.FullName,
                "aspire-skills",
                $".{AspireSkillsInstaller.Version}.lock")));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCacheLockIsContendedAndCancellationWasRequested_ThrowsCancellation()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            Directory.CreateDirectory(cacheRoot);
            using var cacheLock = new FileStream(
                Path.Combine(cacheRoot, $".{AspireSkillsInstaller.Version}.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var retryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new TestSink();
            sink.MessageLogged += context =>
            {
                if (context.Message?.Contains(CacheLockRetryLogMessage, StringComparison.Ordinal) == true)
                {
                    retryObserved.TrySetResult();
                }
            };
            var logger = new TestLogger<AspireSkillsInstaller>(new TestLoggerFactory(sink, enabled: true));
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features,
                logger: logger);
            using var cancellationTokenSource = new CancellationTokenSource();

            var installTask = installer.InstallAsync(cancellationTokenSource.Token);
            await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(installTask.IsCompleted);
            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => installTask);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_RemovesOnlyStaleTemporaryCacheDirectories()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            Directory.CreateDirectory(cacheRoot);
            var temporaryDirectoryPrefixes = new[] { ".github-", ".embedded-", ".extract-", ".stage-" };
            List<string> staleDirectories = [];
            List<string> recentDirectories = [];
            foreach (var prefix in temporaryDirectoryPrefixes)
            {
                var staleDirectory = Directory.CreateDirectory(Path.Combine(cacheRoot, $"{prefix}stale")).FullName;
                Directory.SetLastWriteTimeUtc(staleDirectory, DateTime.UtcNow.AddDays(-8));
                staleDirectories.Add(staleDirectory);
                recentDirectories.Add(Directory.CreateDirectory(Path.Combine(cacheRoot, $"{prefix}recent")).FullName);
            }

            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.All(staleDirectories, directory => Assert.False(Directory.Exists(directory)));
            Assert.All(recentDirectories, directory => Assert.True(Directory.Exists(directory)));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenStaleTemporaryDirectoryIsActive_DoesNotDeleteIt()
    {
        var rootDirectory = CreateTempDirectory();
        var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AspireSkillsInstallResult>? firstInstallTask = null;

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{ComputeSha256(archiveBytes)}"));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier
            {
                VerificationStarted = verificationStarted,
                VerificationGate = verificationGate.Task
            };
            var firstInstaller = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier);

            firstInstallTask = firstInstaller.InstallAsync(CancellationToken.None);
            await verificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "aspire-skills");
            var activeTemporaryDirectory = Assert.Single(Directory.GetDirectories(cacheRoot, ".github-*"));
            Directory.SetLastWriteTimeUtc(activeTemporaryDirectory, DateTime.UtcNow.AddDays(-8));

            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var secondInstaller = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var secondResult = await secondInstaller.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, secondResult.Status);
            Assert.True(Directory.Exists(activeTemporaryDirectory));

            verificationGate.SetResult();
            var firstResult = await firstInstallTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(AspireSkillsInstallStatus.Installed, firstResult.Status);
            Assert.False(Directory.Exists(activeTemporaryDirectory));
            Assert.False(File.Exists($"{activeTemporaryDirectory}.lock"));
        }
        finally
        {
            verificationGate.TrySetResult();
            if (firstInstallTask is { IsCompleted: false })
            {
                await firstInstallTask.WaitAsync(TimeSpan.FromSeconds(10));
            }

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
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubResponseEndsEarly_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var handler = new MockHttpMessageHandler(new HttpIOException(HttpRequestError.ResponseEnded));
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubRequestTimesOut_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var handler = new MockHttpMessageHandler(
                new TaskCanceledException("The request timed out.", new TimeoutException()));
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubRequestIsCanceledByCaller_ThrowsCancellation()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var requestObserved = false;
            var handler = new MockHttpMessageHandler(_ =>
            {
                requestObserved = true;
                cancellationTokenSource.Cancel();
                throw new OperationCanceledException(cancellationTokenSource.Token);
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var installer = CreateInstaller(executionContext, httpMessageHandler: handler);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => installer.InstallAsync(cancellationTokenSource.Token));
            Assert.True(requestObserved);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubIsUnavailable_UsesVerifiedGitHubCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var verifiedArchiveSha512 = new string('a', 128);
            var verifiedGitHubCacheDirectory = GetBundleCacheDirectory(executionContext, verifiedArchiveSha512);
            await CreateCachedBundleAsync(
                verifiedGitHubCacheDirectory,
                archiveSha512: verifiedArchiveSha512,
                githubAttestationVerified: true,
                skillBody: "# Verified GitHub");
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Verified GitHub", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubIsUnavailable_UsesMostRecentCompatibleVerifiedCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var compatibleArchiveSha512 = new string('a', 128);
            var compatibleCacheDirectory = GetBundleCacheDirectory(executionContext, compatibleArchiveSha512);
            await CreateCachedBundleAsync(
                compatibleCacheDirectory,
                archiveSha512: compatibleArchiveSha512,
                githubAttestationVerified: true,
                skillBody: "# Compatible GitHub");
            await WriteLastUsedAsync(compatibleCacheDirectory, DateTimeOffset.UtcNow.AddMinutes(-1));

            var incompatibleArchiveSha512 = new string('b', 128);
            var incompatibleCacheDirectory = GetBundleCacheDirectory(executionContext, incompatibleArchiveSha512);
            await CreateCachedBundleAsync(
                incompatibleCacheDirectory,
                supports: new SkillBundleSupports
                {
                    AspireCli = ">=99.0.0 <100.0.0",
                    AspireSdk = ">=99.0.0 <100.0.0"
                },
                archiveSha512: incompatibleArchiveSha512,
                githubAttestationVerified: true,
                skillBody: "# Incompatible GitHub");
            await WriteLastUsedAsync(incompatibleCacheDirectory, DateTimeOffset.UtcNow);

            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Compatible GitHub", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubDigestIdentifiesStaleCacheAndAssetIsUnavailable_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var currentArchiveBytes = await CreateBundleArchiveBytesAsync(skillBody: "# Current GitHub");
            var currentArchiveSha256 = ComputeSha256(currentArchiveBytes);
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var staleArchiveSha256 = new string('a', 64);
            var staleArchiveSha512 = new string('a', 128);
            var verifiedGitHubCacheDirectory = GetBundleCacheDirectory(executionContext, staleArchiveSha512);
            await CreateCachedBundleAsync(
                verifiedGitHubCacheDirectory,
                archiveSha512: staleArchiveSha512,
                githubArchiveSha256: staleArchiveSha256,
                githubAttestationVerified: true,
                skillBody: "# Stale GitHub");
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{currentArchiveSha256}"));
                }

                assetDownloadRequested = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(assetDownloadRequested);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not-a-sha256-digest", false)]
    [InlineData(null, true)]
    public async Task InstallAsync_WhenReleaseDigestCannotIdentifyUnavailableAsset_UsesEmbeddedBundle(
        string? releaseDigest,
        bool assetRequestTimesOut)
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var staleArchiveSha512 = new string('a', 128);
            var verifiedGitHubCacheDirectory = GetBundleCacheDirectory(executionContext, staleArchiveSha512);
            await CreateCachedBundleAsync(
                verifiedGitHubCacheDirectory,
                archiveSha512: staleArchiveSha512,
                githubArchiveSha256: new string('a', 64),
                githubAttestationVerified: true,
                skillBody: "# Stale GitHub");
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        releaseDigest));
                }

                assetDownloadRequested = true;
                if (assetRequestTimesOut)
                {
                    throw new TaskCanceledException("The request timed out.", new TimeoutException());
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(assetDownloadRequested);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenRemoteFetchFeatureIsDisabled_SkipsGitHubAndUsesEmbedded()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var attestationVerifier = new TestGitHubArtifactAttestationVerifier();
            // Throw on any HTTP call so we can prove the GitHub path was never invoked.
            var handler = new MockHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called when remote fetch is disabled."));
            var features = new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, false);
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                githubArtifactAttestationVerifier: attestationVerifier,
                embeddedBundleProvider: embeddedBundleProvider,
                features: features);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            Assert.False(attestationVerifier.VerifyCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedAspireSkillsBundleProvider_CreatesBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var bundleDirectory = new DirectoryInfo(Path.Combine(rootDirectory, "bundle"));
            var provider = new EmbeddedAspireSkillsBundleProvider(
                new AspireSkillsBundleProvider(),
                NullLogger<EmbeddedAspireSkillsBundleProvider>.Instance);

            var metadata = Assert.IsType<EmbeddedAspireSkillsBundleMetadata>(provider.Metadata);
            var bundle = await provider.CreateBundleAsync(bundleDirectory, CancellationToken.None);

            Assert.NotNull(bundle);
            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
            Assert.Equal(AspireSkillsInstaller.Version, metadata.Version);
            Assert.Equal(AspireSkillsInstaller.GitHubRepository, metadata.Repository);
            Assert.Matches("^[0-9a-f]{128}$", metadata.Sha512);
            AssertNoTemporaryEntries(rootDirectory, "embedded");
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedAspireSkillsBundleProvider_WhenTemporaryArchiveIsLocked_CanPromoteBundle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");

        var rootDirectory = CreateTempDirectory();
        ArchiveLockingAspireSkillsBundleProvider? lockingBundleProvider = null;

        try
        {
            var stageDirectory = new DirectoryInfo(Path.Combine(rootDirectory, ".stage-test"));
            var targetDirectory = Path.Combine(rootDirectory, "cached");
            lockingBundleProvider = new ArchiveLockingAspireSkillsBundleProvider(new AspireSkillsBundleProvider());
            var provider = new EmbeddedAspireSkillsBundleProvider(
                lockingBundleProvider,
                NullLogger<EmbeddedAspireSkillsBundleProvider>.Instance);

            var bundle = await provider.CreateBundleAsync(stageDirectory, CancellationToken.None);

            Assert.NotNull(bundle);
            Assert.NotEmpty(Directory.GetDirectories(rootDirectory, ".embedded-*"));
            Directory.Move(stageDirectory.FullName, targetDirectory);
            Assert.True(File.Exists(Path.Combine(targetDirectory, "skill-manifest.json")));
        }
        finally
        {
            lockingBundleProvider?.Dispose();
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AspireSkillsBundleProvider_WhenValidationFails_RemovesExtractionDirectory()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archivePath = Path.Combine(rootDirectory, "bundle.tgz");
            var archiveBytes = await CreateBundleArchiveBytesAsync(
                new SkillBundleSupports { AspireCli = ">=99.0.0 <100.0.0" });
            await File.WriteAllBytesAsync(archivePath, archiveBytes);

            var bundleDirectory = new DirectoryInfo(Path.Combine(rootDirectory, "bundle"));
            var provider = new AspireSkillsBundleProvider("13.4.0", "13.4.0");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
                new FileInfo(archivePath),
                bundleDirectory,
                ComputeSha512(archiveBytes),
                CancellationToken.None));

            Assert.Contains("supports Aspire CLI versions", exception.Message);
            AssertNoTemporaryEntries(rootDirectory, "extract");
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AspireSkillsBundleProvider_DoesNotUseExistingDestinationFilesToValidateArchive()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archivePath = Path.Combine(rootDirectory, "bundle.tgz");
            var archiveBytes = await CreateBundleArchiveBytesAsync(includeSkillFile: false);
            await File.WriteAllBytesAsync(archivePath, archiveBytes);

            var bundleDirectory = Path.Combine(rootDirectory, "bundle");
            await CreateCachedBundleAsync(bundleDirectory);
            var provider = new AspireSkillsBundleProvider();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
                new FileInfo(archivePath),
                new DirectoryInfo(bundleDirectory),
                ComputeSha512(archiveBytes),
                CancellationToken.None));

            Assert.Contains("was not found", exception.Message);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedAspireSkillsBundleProvider_WhenCancelled_RemovesTemporaryArchive()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var bundleDirectory = new DirectoryInfo(Path.Combine(rootDirectory, "bundle"));
            var provider = new EmbeddedAspireSkillsBundleProvider(
                new AspireSkillsBundleProvider(),
                NullLogger<EmbeddedAspireSkillsBundleProvider>.Instance);
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateBundleAsync(
                bundleDirectory,
                cancellationTokenSource.Token));

            AssertNoTemporaryEntries(rootDirectory, "embedded");
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubReleaseDigestIsMissing_DownloadsAndCachesComputedDigests()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync();
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            Uri? releaseRequestUri = null;
            Uri? assetRequestUri = null;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    releaseRequestUri = request.RequestUri;
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz"));
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
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
            Assert.NotNull(releaseRequestUri);
            Assert.NotNull(assetRequestUri);
            Assert.Contains("/microsoft/aspire-skills/releases/tags/v0.0.1", releaseRequestUri.AbsolutePath);
            Assert.Equal("https://downloads.example.test/aspire-skills-v0.0.1.tgz", assetRequestUri.AbsoluteUri);
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            Assert.Equal(
                archiveSha512,
                await File.ReadAllTextAsync(Path.Combine(
                    cachedBundleDirectory,
                    AspireSkillsInstaller.ArchiveSha512FileName)));
            Assert.Equal(
                archiveSha256,
                await File.ReadAllTextAsync(Path.Combine(
                    cachedBundleDirectory,
                    AspireSkillsInstaller.GitHubArchiveSha256FileName)));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenGitHubReleaseDigestDoesNotMatchDownload_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveBytes = await CreateBundleArchiveBytesAsync(skillBody: "# Downloaded GitHub");
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{new string('0', 64)}"));
                }

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
            Assert.False(attestationVerifier.VerifyCalled);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenDownloadedManifestIsMalformed_DoesNotUseVerifiedOfflineCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var malformedArchiveBytes = await CreateBundleArchiveBytesAsync(malformedManifest: true);
            var malformedArchiveSha256 = ComputeSha256(malformedArchiveBytes);
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{malformedArchiveSha256}"));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(malformedArchiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var verifiedArchiveSha512 = new string('a', 128);
            await CreateCachedBundleAsync(
                GetBundleCacheDirectory(executionContext, verifiedArchiveSha512),
                archiveSha512: verifiedArchiveSha512,
                githubAttestationVerified: true,
                skillBody: "# Verified Offline");
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
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
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
            var archiveBytes = await CreateBundleArchiveBytesAsync(skillBody: "# Downloaded GitHub");
            var archiveSha256 = ComputeSha256(archiveBytes);
            var archiveSha512 = ComputeSha512(archiveBytes);
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedGitHubDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            await CreateCachedBundleAsync(
                cachedGitHubDirectory,
                archiveSha512: archiveSha512,
                githubArchiveSha256: archiveSha256,
                skillBody: "# Cached GitHub");
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
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
            Assert.False(File.Exists(Path.Combine(
                cachedGitHubDirectory,
                AspireSkillsInstaller.GitHubAttestationVerifiedFileName)));
            var skill = Assert.Single(result.Bundle.GetSkillDefinitions());
            var skillFile = Assert.Single(await result.Bundle.GetSkillFilesAsync(skill, CancellationToken.None));
            Assert.Contains("# Aspire", skillFile.Content, StringComparison.Ordinal);
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
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
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
                Sha512 = new string('0', 128)
            };
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var installer = CreateInstaller(
                executionContext,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Failed, result.Status);
            Assert.NotNull(result.Message);
            Assert.Contains("SHA-512", result.Message, StringComparison.Ordinal);
            Assert.Contains(new string('0', 128), result.Message, StringComparison.Ordinal);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenEmbeddedBundleSupportsRangeExcludesCurrentCli_StillUsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            // Simulate a CLI prerelease whose version falls outside the embedded snapshot's
            // declared `supports` range (e.g., a 13.5.x dogfood build paired with a snapshot
            // stamped ">=13.4.0 <13.5.0"). The embedded path must still install the bundle —
            // otherwise an offline user with a version-mismatched embedded snapshot would lose
            // access to all bundled skills.
            var staleSupports = new SkillBundleSupports
            {
                AspireCli = ">=0.0.1 <0.0.2",
                AspireSdk = ">=0.0.1 <0.0.2"
            };
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync(supports: staleSupports);
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCachedEmbeddedBundleSupportsRangeExcludesCurrentCli_StillUsesCache()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            // The cache is written by the installer itself (either from GitHub or from the
            // embedded snapshot), so the bundle's `supports` range is not an invalidation
            // signal. A stale `supports` on a cached entry with the expected version and
            // archive digest must not force a re-install on every invocation.
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, embeddedBundleProvider.Metadata!.Sha512!);
            await CreateCachedBundleAsync(
                cachedBundleDirectory,
                supports: new SkillBundleSupports
                {
                    AspireCli = ">=0.0.1 <0.0.2",
                    AspireSdk = ">=0.0.1 <0.0.2"
                },
                archiveSha512: embeddedBundleProvider.Metadata!.Sha512);
            var installer = CreateInstaller(executionContext, embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.False(embeddedBundleProvider.CreateBundleCalled);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_WhenCachedGitHubBundleSupportsRangeExcludesCurrentCli_UsesEmbeddedBundle()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archiveSha256 = new string('a', 64);
            var archiveSha512 = new string('a', 128);
            var executionContext = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(rootDirectory));
            var cachedBundleDirectory = GetBundleCacheDirectory(executionContext, archiveSha512);
            await CreateCachedBundleAsync(
                cachedBundleDirectory,
                supports: new SkillBundleSupports
                {
                    AspireCli = ">=99.0.0 <100.0.0",
                    AspireSdk = ">=99.0.0 <100.0.0"
                },
                archiveSha512: archiveSha512,
                githubArchiveSha256: archiveSha256,
                githubAttestationVerified: true);
            var assetDownloadRequested = false;
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.0.1", StringComparison.Ordinal))
                {
                    return CreateJsonResponse(CreateGitHubReleaseJson(
                        "aspire-skills-v0.0.1.tgz",
                        "https://downloads.example.test/aspire-skills-v0.0.1.tgz",
                        $"sha256:{archiveSha256}"));
                }

                assetDownloadRequested = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            var embeddedBundleProvider = await CreateEmbeddedBundleProviderAsync();
            var installer = CreateInstaller(
                executionContext,
                httpMessageHandler: handler,
                embeddedBundleProvider: embeddedBundleProvider);

            var result = await installer.InstallAsync(CancellationToken.None);

            Assert.Equal(AspireSkillsInstallStatus.Installed, result.Status);
            Assert.NotNull(result.Bundle);
            Assert.True(assetDownloadRequested);
            Assert.True(embeddedBundleProvider.CreateBundleCalled);
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
        IEmbeddedAspireSkillsBundleProvider? embeddedBundleProvider = null,
        IFeatures? features = null,
        ILogger<AspireSkillsInstaller>? logger = null)
    {
        return new AspireSkillsInstaller(
            githubArtifactAttestationVerifier ?? new TestGitHubArtifactAttestationVerifier(),
            new MockHttpClientFactory(httpMessageHandler ?? new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new AspireSkillsBundleProvider(executionContext),
            embeddedBundleProvider ?? new TestEmbeddedAspireSkillsBundleProvider(),
            new TestInteractionService(),
            executionContext,
            configuration ?? new ConfigurationBuilder().Build(),
            // Default existing tests to the remote-fetch-enabled path so they continue to
            // exercise the GitHub flow without per-test boilerplate. Tests that want to
            // exercise the production default (flag off) pass an empty TestFeatures.
            features ?? new TestFeatures().SetFeature(KnownFeatures.AspireSkillsRemoteFetchEnabled, true),
            TestTelemetryHelper.CreateInitializedTelemetry(),
            logger ?? NullLogger<AspireSkillsInstaller>.Instance);
    }

    private static async Task CreateCachedBundleAsync(
        string bundleDirectory,
        SkillBundleSupports? supports = null,
        string? archiveSha512 = null,
        string? githubArchiveSha256 = null,
        bool githubAttestationVerified = false,
        string skillBody = "# Aspire")
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);

        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath,
            $$"""
            ---
            name: aspire
            description: "Aspire CLI commands and workflows for distributed apps"
            ---

            {{skillBody}}
            """);

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = supports ?? CreateSupports(),
            Skills =
            [
                new SkillBundleSkill
                {
                    Name = CommonAgentApplicators.AspireSkillName,
                    Description = AspireSkillDescription,
                    Files =
                    [
                        new SkillBundleFile
                        {
                            RelativePath = "SKILL.md",
                            Sha512 = ComputeSha512(skillPath)
                        }
                    ]
                }
            ]
        };

        var manifestJson = JsonSerializer.Serialize(manifest, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest);
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);

        if (archiveSha512 is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, AspireSkillsInstaller.ArchiveSha512FileName), archiveSha512);
        }

        if (githubArchiveSha256 is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, AspireSkillsInstaller.GitHubArchiveSha256FileName),
                githubArchiveSha256);
        }

        if (githubAttestationVerified)
        {
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, AspireSkillsInstaller.GitHubAttestationVerifiedFileName),
                string.Empty);
        }
    }

    private static Task WriteLastUsedAsync(string cacheDirectory, DateTimeOffset lastUsed)
    {
        return File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, ".lastused"),
            lastUsed.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static SkillBundleSupports CreateSupports()
    {
        return new SkillBundleSupports
        {
            AspireCli = ">=0.0.0 <999.0.0",
            AspireSdk = ">=0.0.0 <999.0.0"
        };
    }

    private static async Task<byte[]> CreateBundleArchiveBytesAsync(
        SkillBundleSupports? supports = null,
        string skillBody = "# Aspire",
        bool includeGitHubAttestationMarker = false,
        bool malformedManifest = false,
        bool includeSkillFile = true)
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var bundleDirectory = Path.Combine(rootDirectory, $"aspire-skills-v{AspireSkillsInstaller.Version}");
            await CreateCachedBundleAsync(bundleDirectory, supports, skillBody: skillBody);
            if (includeGitHubAttestationMarker)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(bundleDirectory, AspireSkillsInstaller.GitHubAttestationVerifiedFileName),
                    string.Empty);
            }
            if (malformedManifest)
            {
                await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), "{");
            }
            if (!includeSkillFile)
            {
                File.Delete(Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName, "SKILL.md"));
            }

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

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha512(byte[] bytes)
    {
        return Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    }

    private static string GetVersionCacheDirectory(CliExecutionContext executionContext)
    {
        return Path.Combine(
            executionContext.CacheDirectory.FullName,
            "aspire-skills",
            AspireSkillsInstaller.Version);
    }

    private static string GetBundleCacheDirectory(CliExecutionContext executionContext, string archiveSha512)
    {
        return Path.Combine(
            GetVersionCacheDirectory(executionContext),
            AspireSkillsBundleProvider.NormalizeSha512(archiveSha512));
    }

    private static async Task<TestEmbeddedAspireSkillsBundleProvider> CreateEmbeddedBundleProviderAsync(SkillBundleSupports? supports = null)
    {
        var archiveBytes = await CreateBundleArchiveBytesAsync(supports);
        return new TestEmbeddedAspireSkillsBundleProvider
        {
            Metadata = new EmbeddedAspireSkillsBundleMetadata
            {
                Version = AspireSkillsInstaller.Version,
                Repository = AspireSkillsInstaller.GitHubRepository,
                Tag = $"v{AspireSkillsInstaller.Version}",
                AssetName = $"aspire-skills-v{AspireSkillsInstaller.Version}.tgz",
                Sha512 = ComputeSha512(archiveBytes)
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

    private static string CreateGitHubReleaseJson(string assetName, string downloadUrl, string? digest = null)
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = $"v{AspireSkillsInstaller.Version}",
            assets = new[]
            {
                new
                {
                    name = assetName,
                    browser_download_url = downloadUrl,
                    digest
                }
            }
        });
    }

    private static string CreateTempDirectory()
    {
        return Directory.CreateTempSubdirectory("aspire-skills-installer-test-").FullName;
    }

    private static void AssertNoTemporaryEntries(string rootDirectory, string prefix)
    {
        Assert.Empty(Directory.GetDirectories(rootDirectory, $".{prefix}-*"));
        Assert.Empty(Directory.GetFiles(rootDirectory, $".{prefix}-*.lock"));
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

        public TaskCompletionSource? VerificationStarted { get; init; }

        public Task? VerificationGate { get; init; }

        public async Task<ProvenanceVerificationResult> VerifyAsync(
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

            VerificationStarted?.TrySetResult();
            if (VerificationGate is not null)
            {
                await VerificationGate.WaitAsync(cancellationToken);
            }

            return Result;
        }
    }

    private sealed class TestEmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
    {
        private readonly IAspireSkillsBundleProvider _bundleProvider = new AspireSkillsBundleProvider();

        public EmbeddedAspireSkillsBundleMetadata? Metadata { get; set; }

        public byte[]? ArchiveBytes { get; init; }

        public bool CreateBundleCalled { get; private set; }

        public async Task<AspireSkillsBundle?> CreateBundleAsync(
            DirectoryInfo bundleDirectory,
            CancellationToken cancellationToken)
        {
            CreateBundleCalled = true;
            if (ArchiveBytes is null || string.IsNullOrWhiteSpace(Metadata?.Sha512))
            {
                return null;
            }

            Directory.CreateDirectory(bundleDirectory.FullName);
            var archivePath = Path.Combine(bundleDirectory.FullName, $".test-embedded-{Guid.NewGuid():N}.tgz");
            try
            {
                await File.WriteAllBytesAsync(archivePath, ArchiveBytes, cancellationToken);
                return await _bundleProvider.CreateAsync(
                    new FileInfo(archivePath),
                    bundleDirectory,
                    Metadata.Sha512,
                    cancellationToken,
                    skipCompatibilityCheck: true);
            }
            finally
            {
                File.Delete(archivePath);
            }
        }
    }

    private sealed class ArchiveLockingAspireSkillsBundleProvider(IAspireSkillsBundleProvider inner) : IAspireSkillsBundleProvider, IDisposable
    {
        private FileStream? _archiveLock;

        public async Task<AspireSkillsBundle> CreateAsync(
            FileInfo archive,
            DirectoryInfo bundleDirectory,
            string expectedArchiveSha512,
            CancellationToken cancellationToken,
            bool skipCompatibilityCheck = false)
        {
            _archiveLock = new FileStream(
                archive.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return await inner.CreateAsync(
                archive,
                bundleDirectory,
                expectedArchiveSha512,
                cancellationToken,
                skipCompatibilityCheck);
        }

        public Task<AspireSkillsBundle> LoadAsync(
            DirectoryInfo bundleDirectory,
            CancellationToken cancellationToken,
            bool skipCompatibilityCheck = false)
        {
            return inner.LoadAsync(bundleDirectory, cancellationToken, skipCompatibilityCheck);
        }

        public void Dispose()
        {
            _archiveLock?.Dispose();
        }
    }

}
