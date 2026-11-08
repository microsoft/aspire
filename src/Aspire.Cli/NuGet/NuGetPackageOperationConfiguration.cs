// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

/// <summary>
/// Owns the effective working directory and optional overlay for one package or template invocation.
/// </summary>
internal sealed class NuGetPackageOperationConfiguration : IDisposable
{
    private readonly TemporaryNuGetConfigFile? _temporaryConfig;

    private NuGetPackageOperationConfiguration(
        DirectoryInfo originalWorkingDirectory,
        DirectoryInfo effectiveWorkingDirectory,
        FileInfo? explicitConfigFile,
        FileInfo? configurationFile,
        string cacheIdentity,
        TemporaryNuGetConfigFile? temporaryConfig)
    {
        OriginalWorkingDirectory = originalWorkingDirectory;
        EffectiveWorkingDirectory = effectiveWorkingDirectory;
        ExplicitConfigFile = explicitConfigFile;
        ConfigurationFile = configurationFile;
        CacheIdentity = cacheIdentity;
        _temporaryConfig = temporaryConfig;
    }

    public DirectoryInfo OriginalWorkingDirectory { get; }

    public DirectoryInfo EffectiveWorkingDirectory { get; }

    /// <summary>
    /// Gets a caller-supplied config passed through an explicit <c>--configfile</c> option.
    /// Generated overlays remain null so NuGet discovers the overlay and its parent hierarchy.
    /// </summary>
    public FileInfo? ExplicitConfigFile { get; }

    public FileInfo? ConfigurationFile { get; }

    public string CacheIdentity { get; }

    public static NuGetPackageOperationConfiguration Ambient(
        DirectoryInfo workingDirectory,
        string cacheIdentity)
        => new(
            workingDirectory,
            workingDirectory,
            explicitConfigFile: null,
            configurationFile: null,
            cacheIdentity,
            temporaryConfig: null);

    public static NuGetPackageOperationConfiguration FromTemporaryOverlay(
        DirectoryInfo workingDirectory,
        TemporaryNuGetConfigFile temporaryConfig,
        string ambientCacheIdentity)
        => new(
            workingDirectory,
            temporaryConfig.ConfigFile.Directory!,
            explicitConfigFile: null,
            temporaryConfig.ConfigFile,
            BundleNuGetService.CombineCacheIdentities(
                ambientCacheIdentity,
                temporaryConfig.CacheIdentity),
            temporaryConfig);

    public static NuGetPackageOperationConfiguration FromExistingConfiguration(
        DirectoryInfo workingDirectory,
        FileInfo? configurationFile)
        => new(
            workingDirectory,
            workingDirectory,
            configurationFile,
            configurationFile,
            configurationFile?.FullName ?? "ambient",
            temporaryConfig: null);

    public void Dispose() => _temporaryConfig?.Dispose();
}
