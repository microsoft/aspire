// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Shared;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeNuGetClient : INuGetClient
{
    public int RestoreCallCount { get; private set; }
    public int WriteManifestCallCount { get; private set; }
    public int SearchCallCount { get; private set; }
    public int GetSettingsCallCount { get; private set; }
    public int WriteConfigOverlayCallCount { get; private set; }
    public IReadOnlyList<(string Id, string Version)>? LastRestorePackages { get; private set; }
    public IReadOnlyList<string>? LastRestoreSources { get; private set; }
    public IReadOnlyList<string>? LastNuGetConfigPaths { get; private set; }
    public IReadOnlyList<string>? LastConfiguredRestoreSources { get; private set; }
    public string? LastWorkingDirectory { get; private set; }
    public string? LastSettingsWorkingDirectory { get; private set; }
    public NuGetConfigOverlayRequest? LastConfigOverlayRequest { get; private set; }
    public string? LastConfigOverlayPath { get; private set; }

    public Func<
        IReadOnlyList<(string Id, string Version)>,
        string,
        string?,
        string,
        IReadOnlyList<string>,
        IReadOnlyList<string>,
        string,
        string?,
        IReadOnlyList<string>,
        CancellationToken,
        Task>? RestoreCallback { get; set; }

    public Func<string, string, string, string?, CancellationToken, Task>? WriteManifestCallback { get; init; }

    public Func<
        string,
        bool,
        int,
        IReadOnlyList<string>,
        string?,
        string,
        CancellationToken,
        Task<IReadOnlyList<NuGetSearchResult>>>? SearchCallback { get; init; }

    public Func<string, byte[], NuGetSettingsInfo>? GetSettingsCallback { get; set; }

    public Action<NuGetConfigOverlayRequest, string>? WriteConfigOverlayCallback { get; set; }

    public Task RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> nugetConfigPaths,
        string workingDirectory,
        string? globalPackagesFolderOverride,
        IReadOnlyList<string> sensitiveSources,
        CancellationToken cancellationToken)
    {
        RestoreCallCount++;
        LastRestorePackages = packages;
        LastRestoreSources = sources;
        LastNuGetConfigPaths = nugetConfigPaths;
        LastConfiguredRestoreSources = nugetConfigPaths
            .Where(File.Exists)
            .SelectMany(static path => XDocument.Load(path)
                .Descendants("packageSources")
                .Elements("add")
                .Select(static source => source.Attribute("value")!.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LastWorkingDirectory = workingDirectory;
        return RestoreCallback?.Invoke(
            packages,
            framework,
            runtimeIdentifier,
            outputPath,
            sources,
            nugetConfigPaths,
            workingDirectory,
            globalPackagesFolderOverride,
            sensitiveSources,
            cancellationToken) ?? Task.CompletedTask;
    }

    public Task WriteManifestAsync(
        string assetsFilePath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        WriteManifestCallCount++;
        if (WriteManifestCallback is not null)
        {
            return WriteManifestCallback(assetsFilePath, outputPath, framework, runtimeIdentifier, cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return File.WriteAllTextAsync(
            outputPath,
            """{"managedAssemblies":[],"nativeLibraries":[]}""",
            cancellationToken);
    }

    public Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool prerelease,
        int take,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        SearchCallCount++;
        return SearchCallback?.Invoke(
            query,
            prerelease,
            take,
            explicitSources,
            nugetConfigPath,
            workingDirectory,
            cancellationToken) ?? Task.FromResult<IReadOnlyList<NuGetSearchResult>>([]);
    }

    public NuGetSettingsInfo GetSettings(string workingDirectory, byte[] sourceIdentityKey)
    {
        GetSettingsCallCount++;
        LastSettingsWorkingDirectory = workingDirectory;
        return GetSettingsCallback?.Invoke(workingDirectory, sourceIdentityKey)
            ?? new(
                [],
                "settings",
                [],
                [],
                PackageSourceMappingEnabled: false,
                [],
                [],
                [],
                sourceIdentityKey);
    }

    public void WriteConfigOverlay(NuGetConfigOverlayRequest request, string outputPath)
    {
        WriteConfigOverlayCallCount++;
        LastConfigOverlayRequest = request;
        LastConfigOverlayPath = outputPath;
        if (WriteConfigOverlayCallback is not null)
        {
            WriteConfigOverlayCallback(request, outputPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, "<configuration />");
    }
}
