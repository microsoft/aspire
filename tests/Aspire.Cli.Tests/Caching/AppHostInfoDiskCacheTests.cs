// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Caching;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Caching;

public class AppHostInfoDiskCacheTests(ITestOutputHelper outputHelper)
{
    private static AppHostInfoDiskCache CreateCache(TemporaryWorkspace workspace, IConfigurationService? configurationService = null)
    {
        var ctx = workspace.CreateExecutionContext();
        return new AppHostInfoDiskCache(NullLogger<AppHostInfoDiskCache>.Instance, ctx, configurationService ?? new TestConfigurationService());
    }

    private static AppHostInfoDiskCache CreateCacheWithRealConfigurationService(TemporaryWorkspace workspace, Dictionary<string, string?>? processConfigurationValues = null)
    {
        var ctx = workspace.CreateExecutionContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(processConfigurationValues ?? new Dictionary<string, string?>())
            .Build();
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json"));
        var configurationService = new ConfigurationService(configuration, ctx, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        return new AppHostInfoDiskCache(NullLogger<AppHostInfoDiskCache>.Instance, ctx, configurationService);
    }

    private static FileInfo CreateProjectFile(TemporaryWorkspace workspace, string name = "Test.AppHost.csproj")
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, name);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private static AppHostInfoCacheEntry SampleEntry() => new()
    {
        ExitCode = 0,
        IsAspireHost = true,
        AspireHostingVersion = "9.5.0",
        IsUsingCliBundle = false,
        UserSecretsId = "12345",
    };

    [Fact]
    public async Task CacheMissThenHit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        var miss = await cache.TryGetAsync(projectFile, CancellationToken.None).DefaultTimeout();
        Assert.Null(miss);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        // FileInfo caches metadata on first stat, so create a fresh instance after the write.
        var freshProject = new FileInfo(projectFile.FullName);
        var hit = await cache.TryGetAsync(freshProject, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(hit);
        Assert.True(hit!.IsAspireHost);
        Assert.Equal("9.5.0", hit.AspireHostingVersion);
        Assert.Equal("12345", hit.UserSecretsId);
    }

    [Fact]
    public async Task TouchingProjectFileInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        // Make sure the mtime tick actually changes (filesystem resolution can be coarse).
        await Task.Delay(50).DefaultTimeout();
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n<!-- edit -->");
        File.SetLastWriteTimeUtc(projectFile.FullName, DateTime.UtcNow.AddSeconds(2));

        var freshProject = new FileInfo(projectFile.FullName);
        var hit = await cache.TryGetAsync(freshProject, CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task TouchingProjectAssetsJsonInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        // Simulate a `dotnet restore` writing obj/project.assets.json next to the .csproj.
        var objDir = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "obj"));
        var assetsPath = Path.Combine(objDir.FullName, "project.assets.json");
        File.WriteAllText(assetsPath, "{}");

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task TouchingDirectoryPackagesPropsInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        // Drop a Directory.Packages.props next to the project; the cache key walks up to
        // catch this even when nothing else changed.
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"), "<Project />");

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task DisabledCacheNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), """
        {
          "dotnetAppHostInfoCacheDisabled": "true"
        }
        """);
        var cache = CreateCacheWithRealConfigurationService(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(projectFile, CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);

        // Nothing should have been written to disk.
        var cacheDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "apphost-info");
        Assert.False(Directory.Exists(cacheDir) && Directory.EnumerateFiles(cacheDir).Any());
    }

    [Fact]
    public async Task ProcessConfigurationValueDoesNotDisableCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCacheWithRealConfigurationService(workspace, new Dictionary<string, string?>
        {
            ["dotnetAppHostInfoCacheDisabled"] = "true",
        });
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(projectFile, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(hit);
    }

    [Fact]
    public async Task ConcurrentWritesToSameKeyLeaveReadableCacheEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
        {
            var entry = SampleEntry() with
            {
                AspireHostingVersion = $"9.5.{i}",
                UserSecretsId = $"secrets-{i}",
            };
            return cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), entry, CancellationToken.None);
        })).DefaultTimeout();

        var cacheDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "apphost-info");
        Assert.Empty(Directory.EnumerateFiles(cacheDir, "*.tmp"));

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.NotNull(hit);
        Assert.StartsWith("9.5.", hit!.AspireHostingVersion, StringComparison.Ordinal);
        Assert.StartsWith("secrets-", hit.UserSecretsId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_SkipsWriteWhenProjectKeyChangesAfterEvaluation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);
        var expectedKey = cache.GetCacheKey(projectFile);

        await Task.Delay(50).DefaultTimeout();
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n<!-- edit -->");
        File.SetLastWriteTimeUtc(projectFile.FullName, DateTime.UtcNow.AddSeconds(2));

        await cache.SetAsync(new FileInfo(projectFile.FullName), expectedKey, SampleEntry(), CancellationToken.None).DefaultTimeout();

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public void ComputeKey_IsStableForUnchangedInputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = CreateProjectFile(workspace);

        var key1 = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName));
        var key2 = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName));
        Assert.Equal(key1, key2);
        Assert.NotEmpty(key1);
    }

    [Fact]
    public void ComputeKey_DiffersByProjectPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectA = CreateProjectFile(workspace, "A.csproj");
        var projectB = CreateProjectFile(workspace, "B.csproj");

        var keyA = AppHostInfoDiskCache.ComputeKeyAsync(projectA);
        var keyB = AppHostInfoDiskCache.ComputeKeyAsync(projectB);
        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void ComputeKey_WalksAboveTenParentDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectDir = workspace.WorkspaceRoot.FullName;
        for (var i = 0; i < 11; i++)
        {
            projectDir = Directory.CreateDirectory(Path.Combine(projectDir, $"level-{i}")).FullName;
        }

        var projectFile = new FileInfo(Path.Combine(projectDir, "Deep.AppHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var keyBeforeImport = AppHostInfoDiskCache.ComputeKeyAsync(projectFile);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Build.props"), "<Project />");
        var keyAfterImport = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName));

        Assert.NotEqual(keyBeforeImport, keyAfterImport);
    }

    [Fact]
    public void ComputeKey_StopsAtGitFileBoundary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var parentImport = Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Build.props");
        File.WriteAllText(parentImport, "<Project />");

        var repoRoot = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "worktree-root"));
        File.WriteAllText(
            Path.Combine(repoRoot.FullName, ".git"),
            $"gitdir: {Path.Combine(workspace.WorkspaceRoot.FullName, ".git", "worktrees", "worktree-root")}");

        var projectDir = Directory.CreateDirectory(Path.Combine(repoRoot.FullName, "src", "AppHost"));
        var projectFile = new FileInfo(Path.Combine(projectDir.FullName, "AppHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var keyBeforeParentImportEdit = AppHostInfoDiskCache.ComputeKeyAsync(projectFile);

        File.WriteAllText(parentImport, "<Project><!-- above git boundary edit --></Project>");
        File.SetLastWriteTimeUtc(parentImport, DateTime.UtcNow.AddSeconds(2));

        var keyAfterParentImportEdit = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName));

        Assert.Equal(keyBeforeParentImportEdit, keyAfterParentImportEdit);
    }
}
