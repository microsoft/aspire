// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Caching;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Projects;

public sealed class AppHostInfoResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetAppHostInfoAsync_UsesDiskCacheWhenPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => throw new InvalidOperationException("MSBuild should not run on disk cache hit."),
        };
        var diskCache = new TestAppHostInfoDiskCache
        {
            Entry = new AppHostInfoCacheEntry
            {
                ExitCode = 0,
                IsAspireHost = true,
                AspireHostingVersion = "9.5.0",
                IsUsingCliBundle = true,
                UserSecretsId = "secrets",
                RunCommand = "/repo/bin/AppHost",
                TargetPath = "/repo/bin/AppHost.dll",
                RunWorkingDirectory = "/repo/src/AppHost",
                RunArguments = "--from-msbuild",
                TargetFramework = "net10.0",
                TargetFrameworks = null,
            },
        };
        var resolver = new AppHostInfoResolver(runner, diskCache);

        var info = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.True(info.IsAspireHost);
        Assert.Equal("9.5.0", info.AspireHostingVersion);
        Assert.True(info.IsUsingCliBundle);
        Assert.Equal("secrets", info.UserSecretsId);
        Assert.Equal("/repo/bin/AppHost", info.RunCommand);
        Assert.Equal("/repo/bin/AppHost.dll", info.TargetPath);
        Assert.Equal("/repo/src/AppHost", info.RunWorkingDirectory);
        Assert.Equal("--from-msbuild", info.RunArguments);
        Assert.Equal("net10.0", info.TargetFramework);
    }

    [Fact]
    public async Task GetAppHostInfoAsync_CachesSuccessfulMsBuildEvaluationInMemory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        var msbuildCalls = 0;
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                msbuildCalls++;
                return (0, CreateAppHostInfoJson());
            },
        };
        var resolver = new AppHostInfoResolver(runner, new NullAppHostInfoDiskCache());

        var first = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();
        var second = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.True(first.IsAspireHost);
        Assert.True(second.IsAspireHost);
        Assert.Equal(1, msbuildCalls);
    }

    [Fact]
    public async Task GetAppHostInfoAsync_DoesNotCacheFailedMsBuildEvaluationInMemory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        var msbuildCalls = 0;
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                msbuildCalls++;
                return msbuildCalls == 1
                    ? (1, null)
                    : (0, CreateAppHostInfoJson());
            },
        };
        var resolver = new AppHostInfoResolver(runner, new NullAppHostInfoDiskCache());

        var failed = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();
        var succeeded = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, failed.ExitCode);
        Assert.True(succeeded.IsAspireHost);
        Assert.Equal(2, msbuildCalls);
    }

    [Fact]
    public async Task GetAppHostInfoAsync_CoalescesConcurrentMsBuildEvaluationInMemory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<(int ExitCode, JsonDocument? Output)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var msbuildCalls = 0;
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackAsync = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref msbuildCalls);
                started.TrySetResult();
                return complete.Task;
            },
        };
        var resolver = new AppHostInfoResolver(runner, new NullAppHostInfoDiskCache());

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None))
            .ToArray();

        await started.Task.DefaultTimeout();
        complete.SetResult((0, CreateAppHostInfoJson()));
        var results = await Task.WhenAll(tasks).DefaultTimeout();

        Assert.All(results, result => Assert.True(result.IsAspireHost));
        Assert.Equal(1, msbuildCalls);
    }

    [Fact]
    public async Task GetAppHostInfoAsync_CallerCancellationDoesNotCancelSharedEvaluation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<(int ExitCode, JsonDocument? Output)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var msbuildCalls = 0;
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackAsync = (_, _, _, _, cancellationToken) =>
            {
                Assert.False(cancellationToken.CanBeCanceled);
                Interlocked.Increment(ref msbuildCalls);
                started.TrySetResult();
                return complete.Task;
            },
        };
        var resolver = new AppHostInfoResolver(runner, new NullAppHostInfoDiskCache());

        using var cancellationTokenSource = new CancellationTokenSource();
        var canceledWaiter = resolver.GetAppHostInfoAsync(projectFile, cancellationTokenSource.Token);

        await started.Task.DefaultTimeout();
        await cancellationTokenSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWaiter).DefaultTimeout();

        complete.SetResult((0, CreateAppHostInfoJson()));
        var uncanceledWaiter = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.True(uncanceledWaiter.IsAspireHost);
        Assert.Equal(1, msbuildCalls);
    }

    [Fact]
    public async Task GetAppHostInfoAsync_RequestsComputeRunArgumentsTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);
        string[]? capturedTargets = null;
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, targets, _, _) =>
            {
                capturedTargets = targets;
                return (0, CreateAppHostInfoJson());
            },
        };
        var resolver = new AppHostInfoResolver(runner, new NullAppHostInfoDiskCache());

        var info = await resolver.GetAppHostInfoAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.True(info.IsAspireHost);
        // The direct-launch path reads RunCommand/RunArguments/RunWorkingDirectory, which the
        // SDK only populates after ComputeRunArguments has run. The resolver must request that
        // target on its single MSBuild probe so the cached run metadata is correct.
        Assert.NotNull(capturedTargets);
        Assert.Contains("ComputeRunArguments", capturedTargets);
    }

    private static FileInfo CreateProjectFile(TemporaryWorkspace workspace)
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost.csproj");
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private static JsonDocument CreateAppHostInfoJson()
    {
        return JsonDocument.Parse("""
            {
              "Properties": {
                "IsAspireHost": "true",
                "AspireHostingSDKVersion": "9.5.0",
                "AspireUseCliBundle": "true",
                "UserSecretsId": "secrets",
                "RunCommand": "/repo/bin/AppHost",
                "TargetPath": "/repo/bin/AppHost.dll",
                "RunWorkingDirectory": "/repo/src/AppHost",
                "RunArguments": "--from-msbuild",
                "TargetFramework": "net10.0"
              },
              "Items": {}
            }
            """);
    }

    private sealed class TestAppHostInfoDiskCache : IAppHostInfoDiskCache
    {
        public AppHostInfoCacheEntry? Entry { get; init; }

        public string GetCacheKey(FileInfo projectFile)
            => "test-key";

        public Task<AppHostInfoCacheEntry?> TryGetAsync(FileInfo projectFile, CancellationToken cancellationToken)
            => Task.FromResult(Entry);

        public Task SetAsync(FileInfo projectFile, string expectedCacheKey, AppHostInfoCacheEntry entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
