// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Projects;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestProjectLocator : IProjectLocator
{
    public Func<FileInfo?, bool, CancellationToken, Task<FileInfo?>>? UseOrFindAppHostProjectFileAsyncCallback { get; set; }

    public Func<FileInfo?, MultipleAppHostProjectsFoundBehavior, bool, CancellationToken, Task<AppHostProjectSearchResult>>? UseOrFindAppHostProjectFileWithBehaviorAsyncCallback { get; set; }

    public Func<CancellationToken, Task<FileInfo?>>? GetAppHostFromSettingsAsyncCallback { get; set; }

    public Func<DirectoryInfo, AppHostDiscoveryScope, CancellationToken, Task<List<AppHostProjectCandidate>>>? FindAppHostProjectsAsyncCallback { get; set; }

    public Func<DirectoryInfo, AppHostDiscoveryScope, Action<int>?, CancellationToken, IAsyncEnumerable<AppHostProjectCandidate>>? FindAppHostProjectsStreamAsyncCallback { get; set; }

    public Func<DirectoryInfo, AppHostDiscoveryScope, CancellationToken, Task<List<FileInfo>>>? FindAppHostProjectFilesAsyncCallback { get; set; }

    public Func<DirectoryInfo, AppHostDiscoveryScope, int?, CancellationToken, Task<List<FileInfo>>>? FindAppHostProjectFilesWithDepthAsyncCallback { get; set; }

    public async Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        CancellationToken cancellationToken)
    {
        if (FindAppHostProjectsAsyncCallback != null)
        {
            return await FindAppHostProjectsAsyncCallback(searchDirectory, scope, cancellationToken);
        }

        return [];
    }

    public async IAsyncEnumerable<AppHostProjectCandidate> FindAppHostProjectsStreamAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        Action<int>? onDirectoryEnumerated = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (FindAppHostProjectsStreamAsyncCallback is not null)
        {
            await foreach (var candidate in FindAppHostProjectsStreamAsyncCallback(searchDirectory, scope, onDirectoryEnumerated, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return candidate;
            }

            yield break;
        }

        var candidates = await FindAppHostProjectsAsync(searchDirectory, scope, cancellationToken);
        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        if (FindAppHostProjectFilesAsyncCallback != null)
        {
            return await FindAppHostProjectFilesAsyncCallback(searchDirectory, scope, cancellationToken);
        }

        return [];
    }

    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken)
    {
        if (FindAppHostProjectFilesWithDepthAsyncCallback != null)
        {
            return await FindAppHostProjectFilesWithDepthAsyncCallback(searchDirectory, scope, maxDepth, cancellationToken);
        }

        return await FindAppHostProjectFilesAsync(searchDirectory, scope, cancellationToken);
    }

    public async Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
    {
        if (UseOrFindAppHostProjectFileAsyncCallback != null)
        {
            return await UseOrFindAppHostProjectFileAsyncCallback(projectFile, createSettingsFile, cancellationToken);
        }

        // Fallback behavior if not overridden.
        if (projectFile != null)
        {
            return projectFile;
        }

        var fakeProjectFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "AppHost.csproj");
        return new FileInfo(fakeProjectFilePath);
    }

    public async Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        if (UseOrFindAppHostProjectFileWithBehaviorAsyncCallback != null)
        {
            return await UseOrFindAppHostProjectFileWithBehaviorAsyncCallback(projectFile, multipleAppHostProjectsFoundBehavior, createSettingsFile, cancellationToken);
        }

        // Fallback behavior
        var appHostFile = await UseOrFindAppHostProjectFileAsync(projectFile, createSettingsFile, cancellationToken).DefaultTimeout();
        if (appHostFile is null)
        {
            return new AppHostProjectSearchResult(null, []);
        }

        return new AppHostProjectSearchResult(appHostFile, [appHostFile]);
    }

    public async Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (GetAppHostFromSettingsAsyncCallback != null)
        {
            return await GetAppHostFromSettingsAsyncCallback(cancellationToken);
        }

        // Default: no settings file found
        return null;
    }

    public async Task<FileInfo?> GetAppHostFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, CancellationToken cancellationToken = default)
    {
        return await GetAppHostFromSettingsAsync(cancellationToken);
    }
}
