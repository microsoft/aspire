// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Git;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class AppHostCandidateFinderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task FindCandidateFilesAsync_WithEmptyPatterns_ReturnsEmptyResultAndDoesNotCallGit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var gitCalled = false;
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) =>
            {
                gitCalled = true;
                return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>());
            }
        };

        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, [], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.Files);
        Assert.Empty(result.CountsByPattern);
        Assert.False(gitCalled);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_UsesGitIncludedFilesAsSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var gitListedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var diskOnlyAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "legacy/AppHost.csproj");
        var gitRepository = CreateGitRepository(gitListedAppHost.FullName);
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(gitListedAppHost.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == diskOnlyAppHost.FullName);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_PassesSearchDirectoryToGitRepository()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var searchDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");
        var appHost = await WriteFileAsync(searchDirectory, "App/AppHost.csproj");
        DirectoryInfo? capturedSearchRoot = null;
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (searchRoot, _) =>
            {
                capturedSearchRoot = searchRoot;
                return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>
                {
                    appHost.FullName
                });
            }
        };
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(searchDirectory, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.Equal(searchDirectory.FullName, capturedSearchRoot?.FullName);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_GitMode_AppliesSkipListAndDropsMissingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var skipListedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "Node_Modules/pkg/AppHost.csproj");
        var missingAppHost = Path.Combine(workspace.WorkspaceRoot.FullName, "Removed", "AppHost.csproj");
        var gitRepository = CreateGitRepository(appHost.FullName, skipListedAppHost.FullName, missingAppHost);
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_GitMode_DoesNotApplySkipListToFileName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostNamedLikeExcludedDirectory = await WriteFileAsync(workspace.WorkspaceRoot, "bin");
        var appHostInExcludedDirectory = await WriteFileAsync(workspace.WorkspaceRoot, "obj/bin");
        var gitRepository = CreateGitRepository(appHostNamedLikeExcludedDirectory.FullName, appHostInExcludedDirectory.FullName);
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["bin"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHostNamedLikeExcludedDirectory.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == appHostInExcludedDirectory.FullName);
        Assert.Equal(1, result.CountsByPattern["bin"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_GitMode_ExcludesNuGetCachePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var cachedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "nuget-cache/packages/template/AppHost.csproj");
        var gitRepository = CreateGitRepository(appHost.FullName, cachedAppHost.FullName);
        var finder = CreateFinder(gitRepository);
        var nugetCachePath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget-cache");

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_GitMode_EmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var startedActivities = new List<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));

        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var gitRepository = CreateGitRepository(appHost.FullName);
        var finder = CreateFinder(gitRepository, profilingTelemetry);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(result.Files);

        var discoveryActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.AppHostCandidateDiscovery);
        Assert.Equal(AppHostDiscoveryScope.DefaultFiltered.ToString(), discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryScope));
        Assert.Equal(ProfilingTelemetry.Values.AppHostDiscoverySourceGit, discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoverySource));
        Assert.Equal(1, discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryIncludedFileCount));
        Assert.Equal(1, discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));

        var matchActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.AppHostCandidateGitMatch);
        Assert.Equal(ProfilingTelemetry.Values.AppHostDiscoverySourceGit, matchActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoverySource));
        Assert.Equal(1, matchActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryIncludedFileCount));
        Assert.Equal(1, matchActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
        Assert.Equal("session-1", matchActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_WhenGitUnavailable_FallsBackToFilesystemWithSkipList()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        await WriteFileAsync(workspace.WorkspaceRoot, "node_modules/pkg/AppHost.csproj");
        var gitCallCount = 0;
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) =>
            {
                gitCallCount++;
                return Task.FromResult<IReadOnlySet<string>?>(null);
            }
        };
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.Equal(1, gitCallCount);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_FilesystemWalk_EmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var startedActivities = new List<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));

        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        await WriteFileAsync(workspace.WorkspaceRoot, "node_modules/pkg/AppHost.csproj");
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(null)
        };
        var finder = CreateFinder(gitRepository, profilingTelemetry);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var file = Assert.Single(result.Files);
        Assert.Equal(appHost.FullName, file.FullName);

        var discoveryActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.AppHostCandidateDiscovery);
        Assert.Equal(ProfilingTelemetry.Values.AppHostDiscoverySourceFilesystem, discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoverySource));
        Assert.Equal(1, discoveryActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));

        var walkActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.AppHostCandidateFilesystemWalk);
        Assert.Equal(ProfilingTelemetry.Values.AppHostDiscoverySourceFilesystem, walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoverySource));
        Assert.Equal(true, walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoverySkipListEnabled));
        Assert.Equal(1, walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
        Assert.True((int)walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryWalkFileCount)! >= result.Files.Length);
        Assert.True((int)walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryWalkDirectoryCount)! >= 2);
        Assert.Equal(1, walkActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryWalkSkippedDirectoryCount));
        Assert.Equal("session-1", walkActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Fact]
    public async Task FindCandidateFilesAsync_FilesystemFallback_ExcludesNuGetCacheButNotSiblingPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var siblingAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "nuget-cache-extra/AppHost.csproj");
        var cachedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "nuget-cache/packages/template/AppHost.csproj");
        var finder = CreateFinder();
        var nugetCachePath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget-cache");

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var paths = result.Files.Select(file => file.FullName).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(appHost.FullName, paths);
        Assert.Contains(siblingAppHost.FullName, paths);
        Assert.DoesNotContain(cachedAppHost.FullName, paths);
        Assert.Equal(2, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_FilesystemFallback_ExcludesNuGetCacheWithPlatformPathComparison()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Case-insensitive path comparison only applies to Windows and macOS.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var cachedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "nuget-cache/packages/template/AppHost.csproj");
        var finder = CreateFinder();
        var differentlyCasedNuGetCachePath = Path.Combine(workspace.WorkspaceRoot.FullName, "NUGET-CACHE");

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], differentlyCasedNuGetCachePath, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == cachedAppHost.FullName);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_AllFilesScope_IncludesSkipListedDirectoriesAndDoesNotCallGit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var skipListedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "node_modules/pkg/AppHost.csproj");
        var gitCalled = false;
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) =>
            {
                gitCalled = true;
                return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>());
            }
        };
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        var paths = result.Files.Select(file => file.FullName).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(appHost.FullName, paths);
        Assert.Contains(skipListedAppHost.FullName, paths);
        Assert.False(gitCalled);
        Assert.Equal(2, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_AllFilesScope_ExcludesNuGetCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var cachedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "nuget-cache/packages/template/AppHost.csproj");
        var finder = CreateFinder();
        var nugetCachePath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget-cache");

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        var paths = result.Files.Select(file => file.FullName).ToHashSet();
        Assert.Single(paths);
        Assert.Contains(appHost.FullName, paths);
        Assert.DoesNotContain(cachedAppHost.FullName, paths);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_ExplicitDirectoryScope_AppliesSkipListAndDoesNotCallGit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        await WriteFileAsync(workspace.WorkspaceRoot, "node_modules/pkg/AppHost.csproj");
        var gitCalled = false;
        var gitRepository = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) =>
            {
                gitCalled = true;
                return Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>());
            }
        };
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.ExplicitDirectory, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.False(gitCalled);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_MaxDepthZero_MatchesOnlySearchDirectoryFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "AppHost.csproj");
        var nestedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var finder = CreateFinder();

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.ExplicitDirectory, CancellationToken.None, maxDepth: 0).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(directAppHost.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == nestedAppHost.FullName);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_MaxDepthZero_DoesNotMatchDirectoryAwarePatternsBelowSearchDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nestedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var finder = CreateFinder();

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["App/AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.ExplicitDirectory, CancellationToken.None, maxDepth: 0).DefaultTimeout();

        Assert.Empty(result.Files);
        Assert.DoesNotContain(result.Files, file => file.FullName == nestedAppHost.FullName);
        Assert.Equal(0, result.CountsByPattern["App/AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFilteredGitMode_MaxDepthZero_MatchesOnlySearchDirectoryFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "AppHost.csproj");
        var nestedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var gitRepository = CreateGitRepository(directAppHost.FullName, nestedAppHost.FullName);
        var finder = CreateFinder(gitRepository);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None, maxDepth: 0).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(directAppHost.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == nestedAppHost.FullName);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_ReturnsUniqueFilesAndCountsEveryPattern()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var csAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var tsAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "Ts/apphost.ts");
        var finder = CreateFinder();
        string[] patterns = ["AppHost.csproj", "*.csproj", "apphost.ts", "missing.proj"];

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, patterns, nugetCachePath: null, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        var paths = result.Files.Select(file => file.FullName).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(csAppHost.FullName, paths);
        Assert.Contains(tsAppHost.FullName, paths);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
        Assert.Equal(1, result.CountsByPattern["*.csproj"]);
        Assert.Equal(1, result.CountsByPattern["apphost.ts"]);
        Assert.Equal(0, result.CountsByPattern["missing.proj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_GitMatching_ObservesCancellation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var gitRepository = CreateGitRepository(appHost.FullName);
        var finder = CreateFinder(gitRepository);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task FindCandidateFilesAsync_FilesystemWalk_ObservesCancellation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var finder = CreateFinder();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.AllFiles, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task FindCandidateFilesAsync_PatternWithDirectorySegment_MatchesFromSearchRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        await WriteFileAsync(workspace.WorkspaceRoot, "Other/AppHost.csproj");
        var finder = CreateFinder();

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["App/AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(appHost.FullName, path);
        Assert.Equal(1, result.CountsByPattern["App/AppHost.csproj"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_WithRealGitRepository_ComposesGitignoreMatchingAndSkipList()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var trackedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "App/AppHost.csproj");
        var ignoredAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "legacy/AppHost.csproj");
        var skipListedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "node_modules/pkg/AppHost.csproj");
        var untrackedTypeScriptAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "samples/apphost.ts");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore"), "legacy/\n");

        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "App/AppHost.csproj", ".gitignore");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        var finder = CreateFinderWithRealGit(workspace.WorkspaceRoot);

        var result = await finder.FindCandidateFilesAsync(workspace.WorkspaceRoot, ["AppHost.csproj", "apphost.ts"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var paths = result.Files.Select(file => file.FullName).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(trackedAppHost.FullName, paths);
        Assert.Contains(untrackedTypeScriptAppHost.FullName, paths);
        Assert.DoesNotContain(ignoredAppHost.FullName, paths);
        Assert.DoesNotContain(skipListedAppHost.FullName, paths);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
        Assert.Equal(1, result.CountsByPattern["apphost.ts"]);
    }

    [Fact]
    public async Task FindCandidateFilesAsync_DefaultFiltered_WithRealGitRepository_ScopesSubdirectorySearch()
    {
        await GitTestHelper.EnsureGitAvailableAsync();
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await workspace.InitializeGitAsync().DefaultTimeout();
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.WorkspaceRoot.FullName);

        var scopedAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "src/App/AppHost.csproj");
        var outsideAppHost = await WriteFileAsync(workspace.WorkspaceRoot, "Other/AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "add", "src/App/AppHost.csproj", "Other/AppHost.csproj");
        await GitTestHelper.RunGitAsync(workspace.WorkspaceRoot.FullName, "commit", "-m", "init");

        var searchDirectory = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src"));
        var finder = CreateFinderWithRealGit(workspace.WorkspaceRoot);

        var result = await finder.FindCandidateFilesAsync(searchDirectory, ["AppHost.csproj"], nugetCachePath: null, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var path = Assert.Single(result.Files).FullName;
        Assert.Equal(scopedAppHost.FullName, path);
        Assert.DoesNotContain(result.Files, file => file.FullName == outsideAppHost.FullName);
        Assert.Equal(1, result.CountsByPattern["AppHost.csproj"]);
    }

    private static AppHostCandidateFinder CreateFinder(TestGitRepository? gitRepository = null, ProfilingTelemetry? profilingTelemetry = null)
    {
        return new AppHostCandidateFinder(gitRepository ?? new TestGitRepository(), profilingTelemetry ?? CreateProfilingTelemetry(), NullLogger<AppHostCandidateFinder>.Instance);
    }

    private static AppHostCandidateFinder CreateFinderWithRealGit(DirectoryInfo workingDirectory)
    {
        var executionContext = CreateExecutionContext(workingDirectory);
        var profilingTelemetry = CreateProfilingTelemetry();
        var gitRepository = new GitRepository(executionContext, NullLogger<GitRepository>.Instance, profilingTelemetry);
        return new AppHostCandidateFinder(gitRepository, profilingTelemetry, NullLogger<AppHostCandidateFinder>.Instance);
    }

    private static TestGitRepository CreateGitRepository(params string[] includedPaths)
    {
        return new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(includedPaths.ToHashSet())
        };
    }

    private static async Task<FileInfo> WriteFileAsync(DirectoryInfo root, string relativePath)
    {
        var fullPath = Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "Not a real project file.");
        return new FileInfo(fullPath);
    }

    private static Aspire.Cli.CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        var settingsDirectory = workingDirectory.CreateSubdirectory(".aspire-test-state");
        var hivesDirectory = settingsDirectory.CreateSubdirectory("hives");
        var cacheDirectory = settingsDirectory.CreateSubdirectory("cache");
        var sdksDirectory = settingsDirectory.CreateSubdirectory("sdks");
        var logsDirectory = settingsDirectory.CreateSubdirectory("logs");

        return new Aspire.Cli.CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, sdksDirectory, logsDirectory, "test.log");
    }

    private static ProfilingTelemetry CreateProfilingTelemetry(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return new ProfilingTelemetry(configuration);
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
