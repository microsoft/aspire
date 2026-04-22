// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests;

public class BundleRecoveryTests
{
    [Fact]
    public void BundleService_LooksLikeBundleCorruption_ReturnsTrueForKnownRuntimeFailure()
    {
        Assert.True(BundleService.LooksLikeBundleCorruption(
            error: "Failed to create CoreCLR because System.Private.CoreLib.dll could not be loaded."));
    }

    [Fact]
    public void BundleService_LooksLikeBundleCorruption_ReturnsTrueForManagedAssemblyLoadFailure()
    {
        Assert.True(BundleService.LooksLikeBundleCorruption(
            error: "Could not load file or assembly 'Aspire.Foo'",
            additionalLines: ["/tmp/.aspire/managed/Aspire.Foo.dll"]));
    }

    [Fact]
    public void BundleService_LooksLikeBundleCorruption_ReturnsFalseForUnrelatedFailure()
    {
        Assert.False(BundleService.LooksLikeBundleCorruption(
            error: "The requested package source could not be reached."));
    }

    [Fact]
    public void BundleService_LooksLikeBundleCorruption_ReturnsTrueForIncompleteLayoutWhenBundleRootProvided()
    {
        var layoutDirectory = CreateLayout(includeManagedExecutable: false);

        try
        {
            Assert.True(BundleService.LooksLikeBundleCorruption(bundleRoot: layoutDirectory.FullName));
        }
        finally
        {
            DeleteDirectory(layoutDirectory);
        }
    }

    [Fact]
    public async Task EnsureManagedToolPathAsync_RepairsMissingManagedExecutable()
    {
        var brokenLayout = CreateLayout(includeManagedExecutable: false);
        var repairedLayout = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = brokenLayout.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = repairedLayout.FullName });

            var managedPath = await BundleLayoutRepairHelper.EnsureManagedToolPathAsync(
                bundleService,
                NullLogger.Instance,
                "testing bundle repair",
                CancellationToken.None,
                bundleRoot: brokenLayout.FullName);

            Assert.Equal(Path.Combine(repairedLayout.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)), managedPath);
            Assert.Equal(1, bundleService.ExtractCallCount);
        }
        finally
        {
            DeleteDirectory(brokenLayout);
            DeleteDirectory(repairedLayout);
        }
    }

    [Fact]
    public async Task BundleNuGetPackageCache_RetriesSearchAfterBundleRepair()
    {
        var layoutDirectory = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = layoutDirectory.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = layoutDirectory.FullName });

            var executionFactory = new TestProcessExecutionFactory
            {
                AttemptWithErrorCallback = (attempt, _) => attempt switch
                {
                    1 => (134, null, $"Failed to load System.Private.CoreLib.dll{Environment.NewLine}Path: {Path.Combine(layoutDirectory.FullName, "managed", "System.Private.CoreLib.dll")}"),
                    _ => (0, """{"packages":[{"id":"Aspire.Hosting.Redis","version":"9.2.0","source":"nuget"}],"totalHits":1}""", null)
                }
            };

            var cache = new BundleNuGetPackageCache(
                bundleService,
                new LayoutProcessRunner(executionFactory),
                NullLogger<BundleNuGetPackageCache>.Instance,
                new TestFeatures());

            var packages = await cache.GetPackagesAsync(
                layoutDirectory,
                "Aspire.Hosting.Redis",
                filter: null,
                prerelease: false,
                nugetConfigFile: null,
                useCache: false,
                CancellationToken.None);

            var package = Assert.Single(packages);
            Assert.Equal("Aspire.Hosting.Redis", package.Id);
            Assert.Equal("9.2.0", package.Version);
            Assert.Equal(1, bundleService.ExtractCallCount);
            Assert.Equal(2, executionFactory.AttemptCount);
        }
        finally
        {
            DeleteDirectory(layoutDirectory);
        }
    }

    [Fact]
    public async Task AppHostServerProjectFactory_RepairsBrokenBundleLayout()
    {
#if DEBUG
        ForceRepositoryDetectionResult(null);
#endif

        var appDirectory = Directory.CreateTempSubdirectory("aspire-app-");
        var brokenLayout = CreateLayout(includeManagedExecutable: false);
        var repairedLayout = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = brokenLayout.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = repairedLayout.FullName });

            var bundleNuGetService = new BundleNuGetService(
                bundleService,
                new LayoutProcessRunner(new TestProcessExecutionFactory()),
                new TestFeatures(),
                TestExecutionContextFactory.CreateTestContext(),
                NullLogger<BundleNuGetService>.Instance);

            var factory = new AppHostServerProjectFactory(
                new TestDotNetCliRunner(),
                new MockPackagingService(),
                new TestConfigurationService(),
                bundleService,
                bundleNuGetService,
                new TestDotNetSdkInstaller(),
                NullLoggerFactory.Instance,
                NullLogger<AppHostServerProjectFactory>.Instance);

            var project = await factory.CreateAsync(appDirectory.FullName, CancellationToken.None);

            Assert.IsType<PrebuiltAppHostServer>(project);
            Assert.Equal(1, bundleService.ExtractCallCount);
        }
        finally
        {
            DeleteDirectory(appDirectory);
            DeleteDirectory(brokenLayout);
            DeleteDirectory(repairedLayout);
#if DEBUG
            AspireRepositoryDetector.ResetCache();
#endif
        }
    }

    [Fact]
    public async Task EnsureExtractedAndGetLayoutAsync_LogsWarningWhenNoLayoutCanBeDiscovered()
    {
        using var output = new StringWriter();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new SpectreConsoleLoggerProvider(output)));
        var bundleService = new BundleService(new UnavailableLayoutDiscovery(), loggerFactory.CreateLogger<BundleService>());

        var layout = await bundleService.EnsureExtractedAndGetLayoutAsync(CancellationToken.None);

        Assert.Null(layout);
        var logOutput = output.ToString();
        Assert.Contains("No usable bundle layout could be discovered.", logOutput);
        Assert.Contains("Availability=", logOutput);
    }

    [Fact]
    public async Task AppHostServerProjectFactory_LogsSpecificReasonWhenManagedExecutableIsMissing()
    {
#if DEBUG
        ForceRepositoryDetectionResult(null);
#endif

        var appDirectory = Directory.CreateTempSubdirectory("aspire-app-");
        var brokenLayout = CreateLayout(includeManagedExecutable: false);

        try
        {
            using var output = new StringWriter();
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new SpectreConsoleLoggerProvider(output)));

            var bundleService = new StaticLayoutBundleService(new LayoutConfiguration { LayoutPath = brokenLayout.FullName });

            var bundleNuGetService = new BundleNuGetService(
                bundleService,
                new LayoutProcessRunner(new TestProcessExecutionFactory()),
                new TestFeatures(),
                TestExecutionContextFactory.CreateTestContext(),
                NullLogger<BundleNuGetService>.Instance);

            var factory = new AppHostServerProjectFactory(
                new TestDotNetCliRunner(),
                new MockPackagingService(),
                new TestConfigurationService(),
                bundleService,
                bundleNuGetService,
                new TestDotNetSdkInstaller(),
                loggerFactory,
                loggerFactory.CreateLogger<AppHostServerProjectFactory>());

            await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(appDirectory.FullName, CancellationToken.None));

            var logOutput = output.ToString();
            Assert.Contains("did not provide a usable managed executable at", logOutput);
            Assert.Contains(BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName), logOutput);
            Assert.Contains("Availability=layout is missing required content: managed/", logOutput);
        }
        finally
        {
            DeleteDirectory(appDirectory);
            DeleteDirectory(brokenLayout);
#if DEBUG
            AspireRepositoryDetector.ResetCache();
#endif
        }
    }

    [Fact]
    public async Task AppHostServerProjectFactory_LogsSpecificReasonWhenLayoutDiscoveryReturnsNull()
    {
#if DEBUG
        ForceRepositoryDetectionResult(null);
#endif

        var appDirectory = Directory.CreateTempSubdirectory("aspire-app-");
        var incompleteLayout = Directory.CreateTempSubdirectory("aspire-layout-");

        try
        {
            BundleService.WriteExtractionInProgressMarker(incompleteLayout.FullName, "test-version");

            using var output = new StringWriter();
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new SpectreConsoleLoggerProvider(output)));

            var bundleService = new UnavailableLayoutBundleService(incompleteLayout.FullName);
            var bundleNuGetService = new BundleNuGetService(
                bundleService,
                new LayoutProcessRunner(new TestProcessExecutionFactory()),
                new TestFeatures(),
                TestExecutionContextFactory.CreateTestContext(),
                NullLogger<BundleNuGetService>.Instance);

            var factory = new AppHostServerProjectFactory(
                new TestDotNetCliRunner(),
                new MockPackagingService(),
                new TestConfigurationService(),
                bundleService,
                bundleNuGetService,
                new TestDotNetSdkInstaller(),
                loggerFactory,
                loggerFactory.CreateLogger<AppHostServerProjectFactory>());

            await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(appDirectory.FullName, CancellationToken.None));

            var logOutput = output.ToString();
            Assert.Contains("Bundled layout discovery did not return a usable layout", logOutput);
            Assert.Contains("Availability=layout is marked as extraction in progress", logOutput);
        }
        finally
        {
            DeleteDirectory(appDirectory);
            DeleteDirectory(incompleteLayout);
#if DEBUG
            AspireRepositoryDetector.ResetCache();
#endif
        }
    }

    private static DirectoryInfo CreateLayout(bool includeManagedExecutable)
    {
        var root = Directory.CreateTempSubdirectory("aspire-layout-");
        var managedDirectory = root.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        root.CreateSubdirectory(BundleDiscovery.DcpDirectoryName);

        if (includeManagedExecutable)
        {
            var managedPath = Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            File.WriteAllText(managedPath, "test");
        }

        File.WriteAllText(Path.Combine(root.FullName, BundleService.VersionMarkerFileName), "test");
        return root;
    }

    private static void DeleteDirectory(DirectoryInfo directory)
    {
        if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }

#if DEBUG
    private static void ForceRepositoryDetectionResult(string? repoRoot)
    {
        var detectorType = typeof(AspireRepositoryDetector);
        detectorType.GetField("s_cachedRepoRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, repoRoot);
        detectorType.GetField("s_cacheInitialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, true);
    }
#endif

    private sealed class RepairingBundleService(LayoutConfiguration? initialLayout, LayoutConfiguration? repairedLayout) : IBundleService
    {
        private bool _repaired;

        public bool IsBundle => true;

        public int ExtractCallCount { get; private set; }

        public Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
        {
            ExtractCallCount++;
            _repaired = true;
            return Task.FromResult(BundleExtractResult.Extracted);
        }

        public BundleLayoutState GetLayoutState(string? destinationPath = null)
            => BundleLayoutState.Inspect(destinationPath ?? (_repaired ? repairedLayout?.LayoutPath : initialLayout?.LayoutPath));

        public Task<BundleExtractResult> RepairAsync(string? destinationPath = null, CancellationToken cancellationToken = default)
            => ExtractAsync(destinationPath ?? string.Empty, force: true, cancellationToken);

        public Task<LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_repaired ? repairedLayout : initialLayout);
    }

    private sealed class UnavailableLayoutBundleService(string layoutPath) : IBundleService
    {
        public bool IsBundle => true;

        public Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(BundleExtractResult.ExtractionFailed);

        public BundleLayoutState GetLayoutState(string? destinationPath = null)
            => BundleLayoutState.Inspect(destinationPath ?? layoutPath);

        public Task<BundleExtractResult> RepairAsync(string? destinationPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult(BundleExtractResult.ExtractionFailed);

        public Task<LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<LayoutConfiguration?>(null);
    }

    private sealed class StaticLayoutBundleService(LayoutConfiguration? layout) : IBundleService
    {
        public bool IsBundle => true;

        public Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(BundleExtractResult.AlreadyUpToDate);

        public BundleLayoutState GetLayoutState(string? destinationPath = null)
            => BundleLayoutState.Inspect(destinationPath ?? layout?.LayoutPath);

        public Task<BundleExtractResult> RepairAsync(string? destinationPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult(BundleExtractResult.AlreadyUpToDate);

        public Task<LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(layout);
    }

    private sealed class UnavailableLayoutDiscovery : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => null;

        public bool IsBundleModeAvailable(string? projectDirectory = null) => false;
    }
}
