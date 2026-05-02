// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestProjectLocator : IProjectLocator
{
    public Func<FileInfo?, bool, CancellationToken, Task<FileInfo?>>? UseOrFindAppHostProjectFileAsyncCallback { get; set; }

    public Func<FileInfo?, MultipleAppHostProjectsFoundBehavior, bool, CancellationToken, Task<AppHostProjectSearchResult>>? UseOrFindAppHostProjectFileWithBehaviorAsyncCallback { get; set; }

    public Func<CancellationToken, Task<FileInfo?>>? GetAppHostFromSettingsAsyncCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task<List<AppHostProjectCandidate>>>? FindAppHostProjectsAsyncCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task<List<FileInfo>>>? FindAppHostProjectFilesAsyncCallback { get; set; }

    public async Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, CancellationToken cancellationToken)
    {
        if (FindAppHostProjectsAsyncCallback != null)
        {
            return await FindAppHostProjectsAsyncCallback(searchDirectory, cancellationToken);
        }

        if (FindAppHostProjectFilesAsyncCallback != null)
        {
            var appHostFiles = await FindAppHostProjectFilesAsyncCallback(searchDirectory, cancellationToken);
            return appHostFiles.Select(f => new AppHostProjectCandidate(f, KnownLanguageId.CSharp)).ToList();
        }

        return [];
    }

    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, CancellationToken cancellationToken)
    {
        if (FindAppHostProjectFilesAsyncCallback != null)
        {
            return await FindAppHostProjectFilesAsyncCallback(searchDirectory, cancellationToken);
        }

        return [];
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
}
