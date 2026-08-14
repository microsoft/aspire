// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
        return new AppHostInfoDiskCache(NullLogger<AppHostInfoDiskCache>.Instance, ctx, configurationService ?? new TestConfigurationService(), new TestEnvironment());
    }

    private static AppHostInfoDiskCache CreateCacheWithRealConfigurationService(TemporaryWorkspace workspace, Dictionary<string, string?>? processConfigurationValues = null)
    {
        var ctx = workspace.CreateExecutionContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(processConfigurationValues ?? new Dictionary<string, string?>())
            .Build();
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json"));
        var configurationService = new ConfigurationService(configuration, ctx, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        return new AppHostInfoDiskCache(NullLogger<AppHostInfoDiskCache>.Instance, ctx, configurationService, new TestEnvironment());
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
        RunCommand = "/repo/bin/AppHost",
        TargetPath = "/repo/bin/AppHost.dll",
        RunWorkingDirectory = "/repo/src/AppHost",
        RunArguments = "--from-msbuild",
        TargetFramework = "net10.0",
        TargetFrameworks = "net10.0;net9.0",
    };

    private static IEnumerable<string> EnumerateCacheEntries(TemporaryWorkspace workspace)
    {
        var cacheDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "apphost-info");
        return Directory.Exists(cacheDirectory) ? Directory.EnumerateFiles(cacheDirectory) : [];
    }

    [Fact]
    public async Task CacheMissThenHit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        Assert.Equal("/repo/bin/AppHost", hit.RunCommand);
        Assert.Equal("/repo/bin/AppHost.dll", hit.TargetPath);
        Assert.Equal("/repo/src/AppHost", hit.RunWorkingDirectory);
        Assert.Equal("--from-msbuild", hit.RunArguments);
        Assert.Equal("net10.0", hit.TargetFramework);
    }

    [Fact]
    public async Task CacheEntryMissingRunCommandIsTreatedAsMiss()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { RunCommand = null }, CancellationToken.None).DefaultTimeout();

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();

        Assert.Null(hit);
    }

    [Fact]
    public async Task CachePayloadContainsOnlyProjectInspectionMetadata()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace);

        await cache.SetAsync(
            projectFile,
            cache.GetCacheKey(projectFile),
            SampleEntry() with { IsUsingCliBundle = true },
            CancellationToken.None).DefaultTimeout();

        var cachePath = Assert.Single(EnumerateCacheEntries(workspace));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "aspireHostingVersion",
                "exitCode",
                "isAspireHost",
                "isUsingCliBundle",
                "runArguments",
                "runCommand",
                "runWorkingDirectory",
                "schemaVersion",
                "targetFramework",
                "targetFrameworks",
                "targetPath",
                "userSecretsId",
            ],
            propertyNames);
    }

    [Fact]
    public async Task TouchingProjectFileInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
    public async Task EditingCustomWalkUpImportInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // The prefilter admits this project because of the walk-up import, and MSBuild resolves that import
        // to the Aspire.Common.props two directories above. Flipping IsAspireHost inside that file changes
        // the answer without touching the .csproj, obj/project.assets.json, or any Directory.Build.* /
        // Directory.Packages.* / global.json the conventional walk stats — so unless the custom import is
        // fingerprinted too, the stale "not an AppHost" entry wins forever and the AppHost stays
        // undiscoverable across CLI invocations.
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "MyHost"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        var commonProps = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Common.props");
        File.WriteAllText(commonProps, "<Project />");

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { IsAspireHost = false }, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());

        File.WriteAllText(commonProps, """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(commonProps, DateTime.UtcNow.AddSeconds(2));

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task UnfingerprintableWalkUpImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // Here the searched file name is itself an MSBuild expression, so no filesystem walk can predict
        // which file the import binds to and no mtime can represent it. Caching such a project would make an
        // edit to that unknown file permanently invisible, so the disk cache must opt out entirely.
        var projectFile = CreateProjectFile(workspace, "MyHost.csproj");
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetPathOfFileAbove($(SharedPropsFileName), '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        await cache.SetAsync(new FileInfo(projectFile.FullName), cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();

        Assert.Null(hit);

        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task EditingExactStaticImportInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // The nearest Directory.Build.props carries no marker but imports a Directory.Build.props from a
        // SIBLING directory, so the prefilter cannot prove the project is not an AppHost and admits it.
        // 'shared' is not an ancestor of the project, so the conventional walk never stats the file MSBuild
        // actually evaluates; only fingerprinting the exact resolved path notices the edit.
        //   <root>/shared/Directory.Build.props   the file that decides the verdict
        //   <root>/repo/Directory.Build.props     imports $(MSBuildThisFileDirectory)../shared/Directory.Build.props
        //   <root>/repo/proj/MyHost.csproj
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "proj"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "Directory.Build.props"), """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)../shared/Directory.Build.props" />
            </Project>
            """);

        var sharedDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "shared"));
        var sharedProps = Path.Combine(sharedDirectory.FullName, "Directory.Build.props");
        File.WriteAllText(sharedProps, "<Project />");

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { IsAspireHost = false }, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());

        File.WriteAllText(sharedProps, """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(sharedProps, DateTime.UtcNow.AddSeconds(2));

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task EditingExactStaticImportWithRedundantSeparatorInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "proj"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "Directory.Build.props"), """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)/../shared/Directory.Build.props" />
            </Project>
            """);

        var sharedDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "shared"));
        var sharedProps = Path.Combine(sharedDirectory.FullName, "Directory.Build.props");
        File.WriteAllText(sharedProps, "<Project />");

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { IsAspireHost = false }, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());

        File.WriteAllText(sharedProps, """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(sharedProps, DateTime.UtcNow.AddSeconds(2));

        Assert.Null(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());
    }

    [Fact]
    public async Task WildcardStaticConventionalImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "proj"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "Directory.Build.props"), """
            <Project>
              <Import Project="../Directory.Build.p*" />
            </Project>
            """);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        Assert.Null(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());
        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task UnresolvableStaticImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // <Import Project="$(RepoRoot)Directory.Build.props" /> names a conventional file, so the prefilter
        // admits the project, but only MSBuild knows which directory $(RepoRoot) expands to. There is no
        // path to stat, so the disk cache must opt out instead of storing an entry that an edit to that file
        // could never invalidate.
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "proj"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "Directory.Build.props"), """
            <Project>
              <Import Project="$(RepoRoot)Directory.Build.props" />
            </Project>
            """);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();

        Assert.Null(hit);
        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task EditingAppendedWalkUpImportTargetInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // GetPathOfFileAbove returns a FILE path, so trailing text concatenates onto the file name rather
        // than naming a separate file: the import below resolves to <root>/Aspire.Common.props, not to
        // 'Aspire.Common' plus '.props'. Recording those two pieces separately fingerprints neither the file
        // MSBuild reads nor anything that changes when it is edited.
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "MyHost"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common', '$(MSBuildThisFileDirectory)../')).props" />
            </Project>
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Common"), "");
        var commonProps = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Common.props");
        File.WriteAllText(commonProps, "<Project />");

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { IsAspireHost = false }, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());

        File.WriteAllText(commonProps, """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(commonProps, DateTime.UtcNow.AddSeconds(2));

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task WildcardWalkUpImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // A glob suffix expands to every match at evaluation time, so the import has no single target to
        // stat and creating a new matching file would not move any tracked mtime.
        var projectFile = CreateProjectFile(workspace, "MyHost.csproj");
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Repo.marker'))/*.props" />
            </Project>
            """);

        await cache.SetAsync(new FileInfo(projectFile.FullName), cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();

        Assert.Null(hit);
        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task WrappedWalkUpImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);
        var projectFile = CreateProjectFile(workspace, "MyHost.csproj");
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([System.String]::Concat($([MSBuild]::GetPathOfFileAbove('Anchor', '$(MSBuildThisFileDirectory)../')), '.props'))" />
            </Project>
            """);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        Assert.Null(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());
        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task ShadowedAncestorWithUnfingerprintableImportStillCaches()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // The nearest Directory.Build.props declares no chaining import, so MSBuild never evaluates the
        // outer one and its unresolvable import cannot affect this project. Analyzing every ancestor level
        // instead of the reachable chain would disable caching here for no reason.
        //   <root>/Directory.Build.props        imports $(SharedPropsDir)Directory.Build.props (shadowed)
        //   <root>/repo/Directory.Build.props   no marker, no imports — terminates the chain
        //   <root>/repo/proj/MyHost.csproj
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "proj"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Build.props"), """
            <Project>
              <Import Project="$(SharedPropsDir)Directory.Build.props" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "repo", "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <SomeUnrelated>1</SomeUnrelated>
              </PropertyGroup>
            </Project>
            """);

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.NotNull(hit);
    }

    [Fact]
    public async Task EditingAppendedDirectoryNameWalkUpTargetInvalidatesCacheEntry()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // The mirror image of the GetPathOfFileAbove case: GetDirectoryNameOfFileAbove returns a DIRECTORY,
        // so the suffix must start with a separator and names a file BESIDE the anchor rather than a
        // continuation of the anchor's name. The import below resolves to <root>/Custom.props, selected by
        // <root>/Repo.marker. Both names have to be fingerprinted — the anchor because it decides which
        // ancestor directory is chosen, Custom.props because it is the file MSBuild actually reads.
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "MyHost"));
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyHost.csproj"));
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Repo.marker'))/Custom.props" />
            </Project>
            """);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Repo.marker"), "");
        var customProps = Path.Combine(workspace.WorkspaceRoot.FullName, "Custom.props");
        File.WriteAllText(customProps, "<Project />");

        await cache.SetAsync(projectFile, cache.GetCacheKey(projectFile), SampleEntry() with { IsAspireHost = false }, CancellationToken.None).DefaultTimeout();
        Assert.NotNull(await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout());

        File.WriteAllText(customProps, """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(customProps, DateTime.UtcNow.AddSeconds(2));

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.Null(hit);
    }

    [Fact]
    public async Task NestedSuffixWalkUpImportNeverReadsOrWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // A suffix that descends into a sub-directory ('/build/Custom.props') resolves to a file whose
        // directory the ancestor walk never enumerates, so statting the leaf name at every ancestor level
        // would miss it. There is no single name to fingerprint, so the cache must opt out.
        var projectFile = CreateProjectFile(workspace, "MyHost.csproj");
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Repo.marker'))/build/Custom.props" />
            </Project>
            """);

        await cache.SetAsync(new FileInfo(projectFile.FullName), cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();
        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();

        Assert.Null(hit);
        Assert.Empty(EnumerateCacheEntries(workspace));
    }

    [Fact]
    public async Task UnresolvableStaticImportInProjectFileStillCaches()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var cache = CreateCache(workspace);

        // Only walk-up imports inside the .csproj itself can promote it (IsLikelyAppHost step 1b), so a
        // static import there never changes the prefilter's verdict and must not widen the cache bypass.
        // Ordinary static imports remain this cache's documented pre-existing limitation; they are no worse
        // here than for any other project.
        var projectFile = CreateProjectFile(workspace, "Test.AppHost.csproj");
        File.WriteAllText(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(RepoRoot)Directory.Build.props" />
            </Project>
            """);

        await cache.SetAsync(new FileInfo(projectFile.FullName), cache.GetCacheKey(projectFile), SampleEntry(), CancellationToken.None).DefaultTimeout();

        var hit = await cache.TryGetAsync(new FileInfo(projectFile.FullName), CancellationToken.None).DefaultTimeout();
        Assert.NotNull(hit);
    }

    [Fact]
    public void ComputeKey_IsStableForUnchangedInputs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = CreateProjectFile(workspace);

        var key1 = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName), new TestEnvironment());
        var key2 = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName), new TestEnvironment());
        Assert.Equal(key1, key2);
        Assert.NotEmpty(key1);
    }

    [Fact]
    public void ComputeKey_DiffersByProjectPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectA = CreateProjectFile(workspace, "A.csproj");
        var projectB = CreateProjectFile(workspace, "B.csproj");

        var keyA = AppHostInfoDiskCache.ComputeKeyAsync(projectA, new TestEnvironment());
        var keyB = AppHostInfoDiskCache.ComputeKeyAsync(projectB, new TestEnvironment());
        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void ComputeKey_IsCaseInsensitiveForPath()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Path casing normalization only applies on Windows.");

        // On Windows the same physical project is reached through paths that differ only by case —
        // VS Code launches the CLI with a lowercase drive letter ("c:\...") while a terminal uses an
        // uppercase one ("C:\..."), and segment casing can vary too. Because the filesystem is
        // case-insensitive, all of these must derive the same cache key, otherwise a terminal-
        // populated entry is invisible to a later read for the same project and the AppHost is
        // needlessly re-evaluated. The cache key lowercases the whole path on Windows, so vary both
        // the drive letter and the rest of the path here to mirror that scenario.

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = CreateProjectFile(workspace);

        var fullPath = projectFile.FullName;
        var lowerPath = fullPath.ToLowerInvariant();
        var upperDrivePath = char.ToUpperInvariant(fullPath[0]) + fullPath[1..].ToUpperInvariant();

        var keyLower = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(lowerPath), new TestEnvironment());
        var keyUpper = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(upperDrivePath), new TestEnvironment());

        Assert.Equal(keyLower, keyUpper);
    }

    [Fact]
    public void ComputeKey_WalksAboveTenParentDirectories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var projectDir = workspace.WorkspaceRoot.FullName;
        for (var i = 0; i < 11; i++)
        {
            projectDir = Directory.CreateDirectory(Path.Combine(projectDir, $"level-{i}")).FullName;
        }

        var projectFile = new FileInfo(Path.Combine(projectDir, "Deep.AppHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var keyBeforeImport = AppHostInfoDiskCache.ComputeKeyAsync(projectFile, new TestEnvironment());
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Build.props"), "<Project />");
        var keyAfterImport = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName), new TestEnvironment());

        Assert.NotEqual(keyBeforeImport, keyAfterImport);
    }

    [Fact]
    public void ComputeKey_WalksPastGitFileBoundary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var parentImport = Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Build.props");
        File.WriteAllText(parentImport, "<Project />");

        var repoRoot = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "worktree-root"));
        File.WriteAllText(
            Path.Combine(repoRoot.FullName, ".git"),
            $"gitdir: {Path.Combine(workspace.WorkspaceRoot.FullName, ".git", "worktrees", "worktree-root")}");

        var projectDir = Directory.CreateDirectory(Path.Combine(repoRoot.FullName, "src", "AppHost"));
        var projectFile = new FileInfo(Path.Combine(projectDir.FullName, "AppHost.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var keyBeforeParentImportEdit = AppHostInfoDiskCache.ComputeKeyAsync(projectFile, new TestEnvironment());

        File.WriteAllText(parentImport, "<Project><!-- above git boundary edit --></Project>");
        File.SetLastWriteTimeUtc(parentImport, DateTime.UtcNow.AddSeconds(2));

        var keyAfterParentImportEdit = AppHostInfoDiskCache.ComputeKeyAsync(new FileInfo(projectFile.FullName), new TestEnvironment());

        // MSBuild's Directory.Build.* discovery has no .git boundary, so a Directory.Build.props above a
        // nested .git can still influence evaluation. The fingerprint walk mirrors that range (and
        // DotNetAppHostProject.IsLikelyAppHost's ancestor walk), so editing the file above the .git must
        // change the key — otherwise a classifier that promoted the project would reuse a stale entry.
        Assert.NotEqual(keyBeforeParentImportEdit, keyAfterParentImportEdit);
    }
}
