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
    public async Task BundleNuGetPackageCache_RetriesSearchAfterLayoutBecomesIncomplete()
    {
        var initialLayout = CreateLayout(includeManagedExecutable: true);
        var repairedLayout = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = initialLayout.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = repairedLayout.FullName });

            var executionFactory = new TestProcessExecutionFactory
            {
                AttemptWithErrorCallback = (attempt, _) =>
                {
                    if (attempt == 1)
                    {
                        var managedPath = Path.Combine(initialLayout.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
                        File.Delete(managedPath);

                        return (134, null, "bundle process failed");
                    }

                    return (0, "{\"packages\":[{\"id\":\"Aspire.Hosting.Redis\",\"version\":\"9.2.0\",\"source\":\"nuget\"}],\"totalHits\":1}", null);
                }
            };

            var cache = new BundleNuGetPackageCache(
                bundleService,
                new LayoutProcessRunner(executionFactory),
                NullLogger<BundleNuGetPackageCache>.Instance,
                new TestFeatures());

            var packages = await cache.GetPackagesAsync(
                initialLayout,
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
            DeleteDirectory(initialLayout);
            DeleteDirectory(repairedLayout);
        }
    }

    [Fact]
    public async Task BundleNuGetPackageCache_DoesNotRetryWhenRuntimeFailureLeavesLayoutUsable()
    {
        var layoutDirectory = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = layoutDirectory.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = layoutDirectory.FullName });

            var executionFactory = new TestProcessExecutionFactory
            {
                AttemptWithErrorCallback = (_, _) =>
                    (134, null, $"Failed to load System.Private.CoreLib.dll{Environment.NewLine}Path: {Path.Combine(layoutDirectory.FullName, "managed", "System.Private.CoreLib.dll")}")
            };

            var cache = new BundleNuGetPackageCache(
                bundleService,
                new LayoutProcessRunner(executionFactory),
                NullLogger<BundleNuGetPackageCache>.Instance,
                new TestFeatures());

            await Assert.ThrowsAsync<NuGetPackageCacheException>(() => cache.GetPackagesAsync(
                layoutDirectory,
                "Aspire.Hosting.Redis",
                filter: null,
                prerelease: false,
                nugetConfigFile: null,
                useCache: false,
                CancellationToken.None));

            Assert.Equal(0, bundleService.ExtractCallCount);
            Assert.Equal(1, executionFactory.AttemptCount);
        }
        finally
        {
            DeleteDirectory(layoutDirectory);
        }
    }

    [Fact]
    public async Task RunManagedToolWithRepairAsync_RetriesWhenManagedToolFailsToStartAfterLayoutMutation()
    {
        var initialLayout = CreateLayout(includeManagedExecutable: true);
        var repairedLayout = CreateLayout(includeManagedExecutable: true);

        try
        {
            var bundleService = new RepairingBundleService(
                initialLayout: new LayoutConfiguration { LayoutPath = initialLayout.FullName },
                repairedLayout: new LayoutConfiguration { LayoutPath = repairedLayout.FullName });

            var executionFactory = new TestProcessExecutionFactory();
            executionFactory.CreateExecutionCallback = (args, env, workingDirectory, options) =>
            {
                if (executionFactory.AttemptCount == 1)
                {
                    var originalManagedPath = Path.Combine(initialLayout.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
                    File.Delete(originalManagedPath);

                    return new TestProcessExecution(
                        "aspire-managed",
                        args,
                        env,
                        options,
                        (_, _) => (0, "unexpected", null),
                        () => executionFactory.AttemptCount)
                    {
                        StartReturnValue = false
                    };
                }

                return new TestProcessExecution(
                    "aspire-managed",
                    args,
                    env,
                    options,
                    (_, _) => (0, "recovered", null),
                    () => executionFactory.AttemptCount);
            };

            var (managedPath, exitCode, output, error) = await BundleLayoutRepairHelper.RunManagedToolWithRepairAsync(
                bundleService,
                new LayoutProcessRunner(executionFactory),
                NullLogger.Instance,
                "testing managed tool launch repair",
                ["nuget", "search"],
                cancellationToken: CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal("recovered", output.Trim());
            Assert.Equal(string.Empty, error);
            Assert.Equal(1, bundleService.ExtractCallCount);
            Assert.Equal(2, executionFactory.AttemptCount);
            Assert.Contains(repairedLayout.FullName, managedPath, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(initialLayout);
            DeleteDirectory(repairedLayout);
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
                new TestPackagingService(),
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
                new TestPackagingService(),
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
                new TestPackagingService(),
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
