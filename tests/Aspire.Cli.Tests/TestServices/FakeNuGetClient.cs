// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeNuGetClient : INuGetClient
{
    public int RestoreCallCount { get; private set; }
    public int WriteManifestCallCount { get; private set; }
    public int SearchCallCount { get; private set; }
    public IReadOnlyList<(string Id, string Version)>? LastRestorePackages { get; private set; }
    public IReadOnlyList<string>? LastRestoreSources { get; private set; }
    public string? LastNuGetConfigPath { get; private set; }
    public string? LastWorkingDirectory { get; private set; }

    public Func<
        IReadOnlyList<(string Id, string Version)>,
        string,
        string?,
        string,
        IReadOnlyList<string>,
        string?,
        string,
        CancellationToken,
        Task<IReadOnlyList<RestoredNuGetPackage>>>? RestoreCallback { get; set; }

    public Func<IReadOnlyList<RestoredNuGetPackage>, string, string, string?, CancellationToken, Task>? WriteManifestCallback { get; init; }

    public Func<
        string,
        bool,
        bool,
        int,
        bool,
        IReadOnlyList<string>,
        string?,
        string,
        CancellationToken,
        Task<IReadOnlyList<NuGetSearchResult>>>? SearchCallback { get; init; }

    public Task<IReadOnlyList<RestoredNuGetPackage>> RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        RestoreCallCount++;
        LastRestorePackages = packages;
        LastRestoreSources = sources;
        LastNuGetConfigPath = nugetConfigPath;
        LastWorkingDirectory = workingDirectory;
        return RestoreCallback?.Invoke(
            packages,
            framework,
            runtimeIdentifier,
            outputPath,
            sources,
            nugetConfigPath,
            workingDirectory,
            cancellationToken) ?? Task.FromResult<IReadOnlyList<RestoredNuGetPackage>>([]);
    }

    public Task WriteManifestAsync(
        IReadOnlyList<RestoredNuGetPackage> packages,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        WriteManifestCallCount++;
        if (WriteManifestCallback is not null)
        {
            return WriteManifestCallback(packages, outputPath, framework, runtimeIdentifier, cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return File.WriteAllTextAsync(
            outputPath,
            """{"managedAssemblies":[],"nativeLibraries":[]}""",
            cancellationToken);
    }

    public Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool exactMatch,
        bool prerelease,
        int take,
        bool useCache,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        SearchCallCount++;
        return SearchCallback?.Invoke(
            query,
            exactMatch,
            prerelease,
            take,
            useCache,
            explicitSources,
            nugetConfigPath,
            workingDirectory,
            cancellationToken) ?? Task.FromResult<IReadOnlyList<NuGetSearchResult>>([]);
    }
}
