// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Globalization;
using Aspire.Hosting.UserSecrets;
using Aspire.Hosting.VersionChecking;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Hosting.Tests.VersionChecking;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[Trait("Partition", "4")]
public class VersionCheckServiceTests
{
    [Fact]
    public async Task ExecuteAsync_NewerVersion_DisplayMessage()
    {
        // Arrange
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);
        var options = new DistributedApplicationOptions();
        var service = CreateVersionCheckService(interactionService: interactionService, packageFetcher: packageFetcher, configuration: configurationManager, options: options);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        packagesTcs.TrySetResult([new NuGetPackage { Id = PackageFetcher.PackageId, Version = "100.0.0" }]);

        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        interaction.CompletionTcs.TrySetResult(InteractionResult.Ok(true));

        await service.ExecuteTask!.DefaultTimeout();

        // Assert
        Assert.True(packageFetcher.FetchCalled);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledInConfiguration_NoFetch()
    {
        // Arrange
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.VersionCheckDisabled] = "true"
        });
        var packageFetcher = new TestPackageFetcher();
        var options = new DistributedApplicationOptions();
        var service = CreateVersionCheckService(interactionService: interactionService, packageFetcher: packageFetcher, configuration: configurationManager, options: options);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        await service.ExecuteTask!.DefaultTimeout();

        // Assert
        Assert.False(packageFetcher.FetchCalled);
    }

    [Fact]
    public async Task ExecuteAsync_InsideLastCheckInterval_NoFetch()
    {
        // Arrange
        var currentDate = new DateTimeOffset(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);
        var lastCheckDate = currentDate.AddMinutes(-1);

        var timeProvider = new TestTimeProvider { UtcNow = currentDate };
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VersionCheckService.LastCheckDateKey] = lastCheckDate.ToString("o", CultureInfo.InvariantCulture)
        });

        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);
        var service = CreateVersionCheckService(
            interactionService: interactionService,
            packageFetcher: packageFetcher,
            configuration: configurationManager,
            timeProvider: timeProvider);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        await service.ExecuteTask!.DefaultTimeout();

        interactionService.Interactions.Writer.Complete();

        // Assert
        Assert.False(packageFetcher.FetchCalled);
        Assert.False(interactionService.Interactions.Reader.TryRead(out var _));
    }

    [Fact]
    public async Task ExecuteAsync_InsideLastCheckIntervalHasLastKnown_NoFetchAndDisplayMessage()
    {
        // Arrange
        var currentDate = new DateTimeOffset(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);
        var lastCheckDate = currentDate.AddMinutes(-1);

        var timeProvider = new TestTimeProvider { UtcNow = currentDate };
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VersionCheckService.LastCheckDateKey] = lastCheckDate.ToString("o", CultureInfo.InvariantCulture),
            [VersionCheckService.KnownLatestVersionKey] = "100.0.0"
        });

        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);
        var service = CreateVersionCheckService(
            interactionService: interactionService,
            packageFetcher: packageFetcher,
            configuration: configurationManager,
            timeProvider: timeProvider);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        interaction.CompletionTcs.TrySetResult(InteractionResult.Ok(true));

        await service.ExecuteTask!.DefaultTimeout();

        // Assert
        Assert.False(packageFetcher.FetchCalled);
    }

    [Theory]
    [InlineData("100.0.0", "100.0.0", false)]
    [InlineData("1.0.0", "100.0.0", true)]
    [InlineData("1.0.0", "100.0.0-pre1", false)]
    [InlineData("1.0.0-pre1", "100.0.0-pre1", true)]
    public async Task ExecuteAsync_InsideLastCheckIntervalHasLastKnownPrerelease_NoFetchAndMaybeDisplayMessage(string currentVersion, string lastKnownVersion, bool displayNotification)
    {
        // Arrange
        var currentDate = new DateTimeOffset(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);
        var lastCheckDate = currentDate.AddMinutes(-1);

        var timeProvider = new TestTimeProvider { UtcNow = currentDate };
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VersionCheckService.LastCheckDateKey] = lastCheckDate.ToString("o", CultureInfo.InvariantCulture),
            [VersionCheckService.KnownLatestVersionKey] = lastKnownVersion
        });

        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);
        var service = CreateVersionCheckService(
            interactionService: interactionService,
            packageFetcher: packageFetcher,
            configuration: configurationManager,
            timeProvider: timeProvider,
            packageVersionProvider: new TestPackageVersionProvider(SemVersion.Parse(currentVersion)));

        // Act
        _ = service.StartAsync(CancellationToken.None);

        if (displayNotification)
        {
            var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();
            interaction.CompletionTcs.TrySetResult(InteractionResult.Ok(true));
            await service.ExecuteTask!.DefaultTimeout();
        }
        else
        {
            await service.ExecuteTask!.DefaultTimeout();
            interactionService.Interactions.Writer.Complete();
            Assert.False(interactionService.Interactions.Reader.TryRead(out var _));
        }

        // Assert
        Assert.False(packageFetcher.FetchCalled);
    }

    [Fact]
    public async Task ExecuteAsync_OlderVersion_NoMessage()
    {
        // Arrange
        var interactionService = new TestInteractionService();
        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);

        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VersionCheckService.KnownLatestVersionKey] = "100.0.0" // ignored
        });

        var service = CreateVersionCheckService(interactionService: interactionService, packageFetcher: packageFetcher, configuration: configurationManager);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        packagesTcs.SetResult([new NuGetPackage { Id = PackageFetcher.PackageId, Version = "0.1.0" }]);

        await service.ExecuteTask!.DefaultTimeout();

        interactionService.Interactions.Writer.Complete();

        // Assert
        Assert.True(packageFetcher.FetchCalled);

        Assert.False(interactionService.Interactions.Reader.TryRead(out var _));
    }

    [Fact]
    public async Task ExecuteAsync_IgnoredVersion_NoMessage()
    {
        // Arrange
        var interactionService = new TestInteractionService();
        var configurationManager = new ConfigurationManager();
        configurationManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VersionCheckService.IgnoreVersionKey] = "100.0.0"
        });
        var packagesTcs = new TaskCompletionSource<List<NuGetPackage>>();
        var packageFetcher = new TestPackageFetcher(packagesTcs.Task);
        var service = CreateVersionCheckService(interactionService: interactionService, packageFetcher: packageFetcher, configuration: configurationManager);

        // Act
        _ = service.StartAsync(CancellationToken.None);

        packagesTcs.SetResult([new NuGetPackage { Id = PackageFetcher.PackageId, Version = "100.0.0" }]);

        await service.ExecuteTask!.DefaultTimeout();

        interactionService.Interactions.Writer.Complete();

        // Assert
        Assert.True(packageFetcher.FetchCalled);

        Assert.False(interactionService.Interactions.Reader.TryRead(out var _));
    }

    private static VersionCheckService CreateVersionCheckService(
        IInteractionService? interactionService = null,
        IPackageFetcher? packageFetcher = null,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        DistributedApplicationOptions? options = null,
        IPackageVersionProvider? packageVersionProvider = null,
        IUserSecretsManager? userSecretsManager = null)
    {
        return new VersionCheckService(
            interactionService ?? new TestInteractionService(),
            NullLogger<VersionCheckService>.Instance,
            configuration ?? new ConfigurationManager(),
            options ?? new DistributedApplicationOptions(),
            packageFetcher ?? new TestPackageFetcher(),
            new DistributedApplicationExecutionContext(new DistributedApplicationOperation()),
            timeProvider ?? new TestTimeProvider(),
            packageVersionProvider ?? new TestPackageVersionProvider(),
            userSecretsManager ?? NoopUserSecretsManager.Instance);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public static TestTimeProvider Instance = new TestTimeProvider();

        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }

    private sealed class TestPackageVersionProvider : IPackageVersionProvider
    {
        private readonly SemVersion _version;

        public TestPackageVersionProvider(SemVersion? version = null)
        {
            _version = version ?? new SemVersion(1, 0, 0);
        }

        public SemVersion? GetPackageVersion()
        {
            return _version;
        }
    }

    private sealed class TestPackageFetcher : IPackageFetcher
    {
        private readonly Task<List<NuGetPackage>> _versionTask;

        public bool FetchCalled { get; private set; }

        public TestPackageFetcher(Task<List<NuGetPackage>>? versionTask = null)
        {
            _versionTask = versionTask ?? Task.FromResult<List<NuGetPackage>>([]);
        }

        public Task<List<NuGetPackage>> TryFetchPackagesAsync(string appHostDirectory, CancellationToken cancellationToken)
        {
            FetchCalled = true;
            return _versionTask;
        }
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
