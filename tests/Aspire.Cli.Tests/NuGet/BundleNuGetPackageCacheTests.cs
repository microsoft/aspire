// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Bundles;
using Aspire.Cli.Caching;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetPackageCacheTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SecondCallReturnsCachedResultWithoutInvokingProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var diskCache = new InMemoryDiskCache();
        var processCallCount = 0;

        var cache = CreateCache(diskCache, () =>
        {
            Interlocked.Increment(ref processCallCount);
            return CreateSearchResultJson("Aspire.Hosting.Redis", "9.0.0");
        });

        var result1 = (await cache.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, false, null, CancellationToken.None).DefaultTimeout()).ToList();
        Assert.Single(result1);
        Assert.Equal("Aspire.Hosting.Redis", result1[0].Id);
        Assert.Equal(1, processCallCount);

        var result2 = (await cache.GetIntegrationPackagesAsync(workspace.WorkspaceRoot, false, null, CancellationToken.None).DefaultTimeout()).ToList();
        Assert.Single(result2);
        Assert.Equal("Aspire.Hosting.Redis", result2[0].Id);
        Assert.Equal(1, processCallCount); // Process should NOT have been called again
    }

    [Fact]
    public async Task UseCacheFalseBypassesDiskCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var diskCache = new InMemoryDiskCache();
        var processCallCount = 0;

        var cache = CreateCache(diskCache, () =>
        {
            Interlocked.Increment(ref processCallCount);
            return CreateSearchResultJson("Aspire.Cli", "9.0.0");
        });

        // GetCliPackagesAsync passes useCache: false
        var result1 = (await cache.GetCliPackagesAsync(workspace.WorkspaceRoot, false, null, CancellationToken.None).DefaultTimeout()).ToList();
        Assert.Single(result1);
        Assert.Equal(1, processCallCount);

        var result2 = (await cache.GetCliPackagesAsync(workspace.WorkspaceRoot, false, null, CancellationToken.None).DefaultTimeout()).ToList();
        Assert.Single(result2);
        Assert.Equal(2, processCallCount); // Process SHOULD be called again since cache is bypassed
    }

    [Fact]
    public async Task DifferentWorkingDirectoriesUseSeparateCacheEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var diskCache = new InMemoryDiskCache();
        var processCallCount = 0;

        var cache = CreateCache(diskCache, () =>
        {
            Interlocked.Increment(ref processCallCount);
            return CreateSearchResultJson("Aspire.Hosting.Redis", "9.0.0");
        });

        var dir1 = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "project1"));
        var dir2 = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "project2"));
        dir1.Create();
        dir2.Create();

        await cache.GetIntegrationPackagesAsync(dir1, false, null, CancellationToken.None).DefaultTimeout();
        Assert.Equal(1, processCallCount);

        await cache.GetIntegrationPackagesAsync(dir2, false, null, CancellationToken.None).DefaultTimeout();
        Assert.Equal(2, processCallCount); // Different working directory should miss cache
    }

    private static BundleNuGetPackageCache CreateCache(IDiskCache diskCache, Func<string> processOutputFactory)
    {
        var bundleService = new FakeBundleService();
        var executionFactory = new FakeProcessExecutionFactory(processOutputFactory);
        var layoutProcessRunner = new LayoutProcessRunner(executionFactory);
        var logger = NullLogger<BundleNuGetPackageCache>.Instance;
        var features = new TestFeatures();

        return new BundleNuGetPackageCache(bundleService, layoutProcessRunner, diskCache, logger, features);
    }

    private static string CreateSearchResultJson(string packageId, string version)
    {
        var result = new BundleSearchResult
        {
            Packages =
            [
                new BundlePackageInfo { Id = packageId, Version = version, Source = "nuget.org" }
            ],
            TotalHits = 1
        };
        return JsonSerializer.Serialize(result, BundleSearchJsonContext.Default.BundleSearchResult);
    }

    private sealed class InMemoryDiskCache : IDiskCache
    {
        private readonly Dictionary<string, string> _entries = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            _entries[key] = content;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBundleService : IBundleService
    {
        private readonly string _tempDir;

        public FakeBundleService()
        {
            // Create a layout structure with the managed executable
            _tempDir = Directory.CreateTempSubdirectory("aspire-test-bundle").FullName;
            var managedDir = Path.Combine(_tempDir, "managed");
            Directory.CreateDirectory(managedDir);
            var exeName = OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed";
            File.WriteAllText(Path.Combine(managedDir, exeName), "fake");
        }

        public bool IsBundle => true;

        public Task EnsureExtractedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(BundleExtractResult.AlreadyUpToDate);

        public Task<LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
        {
            var layout = new LayoutConfiguration
            {
                LayoutPath = _tempDir
            };
            return Task.FromResult<LayoutConfiguration?>(layout);
        }
    }

    private sealed class FakeProcessExecutionFactory(Func<string> outputFactory) : IProcessExecutionFactory
    {
        public IProcessExecution CreateExecution(string command, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
        {
            return new FakeProcessExecution(outputFactory, options);
        }
    }

    private sealed class FakeProcessExecution(Func<string> outputFactory, ProcessInvocationOptions options) : IProcessExecution
    {
        private readonly TaskCompletionSource<int> _exitTcs = new();

        public string FileName => "fake-managed";

        public IReadOnlyList<string> Arguments => [];

        public IReadOnlyDictionary<string, string?> EnvironmentVariables => new Dictionary<string, string?>();

        public bool HasExited => _exitTcs.Task.IsCompleted;

        public int ExitCode => _exitTcs.Task.IsCompleted ? _exitTcs.Task.Result : -1;

        public bool Start()
        {
            // Simulate process output and completion
            var output = outputFactory();
            foreach (var line in output.Split('\n'))
            {
                options.StandardOutputCallback?.Invoke(line);
            }
            _exitTcs.SetResult(0);
            return true;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => _exitTcs.Task;

        public void Kill(bool entireProcessTree) { }

        public void Dispose() { }
    }

    private sealed class TestFeatures : IFeatures
    {
        public bool IsFeatureEnabled(string featureName, bool defaultValue = false) => defaultValue;

        public void LogFeatureState() { }
    }
}
