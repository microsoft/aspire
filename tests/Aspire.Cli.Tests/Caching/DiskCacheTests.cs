// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Caching;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Caching;

public class DiskCacheTests(ITestOutputHelper outputHelper)
{
    private static DiskCache CreateCache(TemporaryWorkspace workspace, Action<Dictionary<string,string?>>? configure = null)
    {
        var values = new Dictionary<string,string?>();
        configure?.Invoke(values);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var ctx = workspace.CreateExecutionContext();
        var loggerFactory = NullLoggerFactory.Instance; // no-op logging is fine here
        var logger = loggerFactory.CreateLogger<DiskCache>();
        return new DiskCache(logger, ctx, configuration);
    }

    [Fact]
    public async Task CacheMissThenHit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var key = "query=foo|prerelease=False|take=10|skip=0|nugetConfigHash=abc|cliVersion=1.0";

        var miss = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Null(miss);

        await cache.SetAsync(key, "RESULT-A", CancellationToken.None).DefaultTimeout();
        var hit = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Equal("RESULT-A", hit);
    }

    [Fact]
    public async Task ExpiredEntryReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace, cfg =>
        {
            cfg["PackageSearchCacheExpirySeconds"] = "1"; // expiry window
            cfg["PackageSearchMaxCacheAgeSeconds"] = "3600"; // large max age so only expiry triggers
        });
        var key = "query=bar|prerelease=False|take=10|skip=0|nugetConfigHash=def|cliVersion=1.0";

        await cache.SetAsync(key, "RESULT-B", CancellationToken.None).DefaultTimeout();
        BackdateEntry(workspace);

        var after = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Null(after);
    }

    [Fact]
    public async Task NewerEntrySupersedesOlder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace, cfg =>
        {
            cfg["PackageSearchCacheExpirySeconds"] = "60";
            cfg["PackageSearchMaxCacheAgeSeconds"] = "3600";
        });
        var key = "query=baz|prerelease=False|take=10|skip=0|nugetConfigHash=ghi|cliVersion=1.0";

        await cache.SetAsync(key, "OLD", CancellationToken.None).DefaultTimeout();
        BackdateEntry(workspace);
        await cache.SetAsync(key, "NEW", CancellationToken.None).DefaultTimeout();
        Assert.Equal(2, GetEntries(workspace).Length);

        var val = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Equal("NEW", val);

        Assert.Single(GetEntries(workspace));
    }

    [Fact]
    public async Task ClearRemovesEntries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var key = "query=clear|prerelease=False|take=10|skip=0|nugetConfigHash=jkl|cliVersion=1.0";
        await cache.SetAsync(key, "VALUE", CancellationToken.None).DefaultTimeout();
        var before = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(before);
        await cache.ClearAsync(CancellationToken.None).DefaultTimeout();
        var after = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Null(after);
    }

    [Fact]
    public async Task OldFilesBeyondMaxAgeAreDeletedOnAccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Max age = 1 second so we can simulate cleanup; expiry large to still treat as hit if not cleaned
        var cache = CreateCache(workspace, cfg =>
        {
            cfg["PackageSearchCacheExpirySeconds"] = "300"; // big
            cfg["PackageSearchMaxCacheAgeSeconds"] = "1";   // small
        });
        var key = "query=cleanup|prerelease=False|take=10|skip=0|nugetConfigHash=mno|cliVersion=1.0";
        await cache.SetAsync(key, "VALUE-X", CancellationToken.None).DefaultTimeout();

        var oldName = BackdateEntry(workspace);

        // Trigger Get which should treat it as too old and delete
        var val = await cache.GetAsync(key, CancellationToken.None).DefaultTimeout();
        Assert.Null(val); // treated as miss after cleanup
        Assert.False(File.Exists(oldName));
    }

    private static string[] GetEntries(TemporaryWorkspace workspace)
    {
        var diskPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "nuget-search");
        return Directory.GetFiles(diskPath, "*.json");
    }

    private static string BackdateEntry(TemporaryWorkspace workspace)
    {
        // Cache age comes from the filename (<hash>.<Unix seconds>.json), not filesystem metadata.
        var entry = Assert.Single(GetEntries(workspace));
        var name = Path.GetFileNameWithoutExtension(entry);
        var hash = name[..name.LastIndexOf('.')];
        var oldUnix = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
        var oldName = Path.Combine(Path.GetDirectoryName(entry)!, $"{hash}.{oldUnix}.json");
        File.Move(entry, oldName);
        return oldName;
    }
}
