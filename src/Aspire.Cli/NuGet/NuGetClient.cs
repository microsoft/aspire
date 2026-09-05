// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using NuGet.Credentials;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;
using NuGetLogLevel = NuGet.Common.LogLevel;
using NuGetLogMessage = NuGet.Common.ILogMessage;

namespace Aspire.Cli.NuGet;

internal interface INuGetClient
{
    Task<IReadOnlyList<RestoredNuGetPackage>> RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task WriteManifestAsync(
        IReadOnlyList<RestoredNuGetPackage> packages,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool exactMatch,
        bool prerelease,
        int take,
        bool useCache,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed record RestoredNuGetPackage(
    string Id,
    string Version,
    string InstallPath);

internal sealed record NuGetSearchResult(
    string Id,
    string Version,
    string Source,
    IReadOnlyList<string> AllVersions);

internal sealed class NuGetClient(
    IFeatures features,
    IEnvironment environment,
    ILogger<NuGetClient> logger) : INuGetClient
{
    private const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";
    private readonly NuGetLogger _nuGetLogger = new(logger);
    private static readonly Lock s_credentialServiceLock = new();
    private static bool s_credentialServiceInitialized;

    public async Task<IReadOnlyList<RestoredNuGetPackage>> RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        InitializeCredentialService();
        Directory.CreateDirectory(outputPath);

        var settings = LoadSettings(nugetConfigPath, workingDirectory);
        var packageSources = ResolvePackageSources(settings, sources);
        var repositories = packageSources
            .Select(Repository.Factory.GetCoreV3)
            .ToArray();
        var packageSourceMapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var targetFramework = NuGetFramework.Parse(framework);
        var rootRequirements = packages
            .Select(package => (package.Id, VersionRange.Parse(package.Version)))
            .ToArray();
        var preferredVersions = rootRequirements
            .Where(package => package.Item2.MinVersion is not null)
            .Select(package => new PackageIdentity(package.Id, package.Item2.MinVersion!))
            .ToArray();

        NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);
        NativeAotNuGetTrustStore.Initialize(_nuGetLogger, environment);

        using var cacheContext = new SourceCacheContext();
        var availablePackages = await ResolveDependencyCandidatesAsync(
            rootRequirements,
            repositories,
            packageSourceMapping,
            sources,
            targetFramework,
            cacheContext,
            cancellationToken).ConfigureAwait(false);

        var resolverContext = new PackageResolverContext(
            DependencyBehavior.Lowest,
            rootRequirements.Select(package => package.Id),
            rootRequirements.Select(package => package.Id),
            packagesConfig: [],
            preferredVersions,
            availablePackages.Values,
            packageSources,
            _nuGetLogger);
        var resolvedIdentities = new PackageResolver()
            .Resolve(resolverContext, cancellationToken)
            .ToArray();

        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var pathResolver = new VersionFolderPathResolver(globalPackagesFolder);
        var extractionContext = new PackageExtractionContext(
            PackageSaveMode.Defaultv3,
            XmlDocFileSaveMode.None,
            ClientPolicyContext.GetClientPolicy(settings, _nuGetLogger),
            _nuGetLogger);

        var restoredPackages = new List<RestoredNuGetPackage>(resolvedIdentities.Length);
        foreach (var identity in resolvedIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installPath = pathResolver.GetInstallPath(identity.Id, identity.Version);
            if (installPath is null || !Directory.Exists(installPath))
            {
                var dependencyInfo = availablePackages[identity];
                installPath = await DownloadPackageAsync(
                    dependencyInfo,
                    globalPackagesFolder,
                    pathResolver,
                    extractionContext,
                    cacheContext,
                    cancellationToken).ConfigureAwait(false);
            }

            restoredPackages.Add(new RestoredNuGetPackage(
                identity.Id,
                identity.Version.ToNormalizedString(),
                installPath));
        }

        return restoredPackages;
    }

    public async Task WriteManifestAsync(
        IReadOnlyList<RestoredNuGetPackage> packages,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var targetFramework = NuGetFramework.Parse(framework);
        var runtimeIdentifiers = GetRuntimeIdentifiers(runtimeIdentifier);
        var assets = packages.SelectMany(
            package => ResolvePackageAssets(package, targetFramework, runtimeIdentifiers));

        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (asset.IsManagedAssembly && managedPaths.Add(asset.Path))
            {
                managedAssemblies.Add(new IntegrationPackageManagedAssembly
                {
                    Name = Path.GetFileNameWithoutExtension(asset.Path),
                    Culture = asset.Culture,
                    Path = asset.Path,
                    PackageId = asset.PackageId,
                    PackageVersion = asset.PackageVersion
                });
            }

            if (asset.IsNativeLibrary && nativePaths.Add(asset.Path))
            {
                nativeLibraries.Add(new IntegrationPackageNativeLibrary
                {
                    FileName = Path.GetFileName(asset.Path),
                    Path = asset.Path
                });
            }
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var manifest = IntegrationPackageProbeManifest.Create(managedAssemblies, nativeLibraries);
        await IntegrationPackageProbeManifest.WriteAsync(outputPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
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
        InitializeCredentialService();
        var settings = LoadSettings(nugetConfigPath, workingDirectory);
        var packageSources = LoadPackageSources(settings, explicitSources);
        var sourceSearches = packageSources.Select(source => exactMatch
            ? GetPackageMetadataAsync(source, query, prerelease, useCache, cancellationToken)
            : SearchSourceAsync(
                source,
                query,
                new global::NuGet.Protocol.Core.Types.SearchFilter(prerelease),
                take,
                cancellationToken));

        var sourceResults = await Task.WhenAll(sourceSearches).ConfigureAwait(false);
        if (exactMatch)
        {
            return sourceResults
                .SelectMany(results => results)
                .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(package => NuGetVersion.Parse(package.Version))
                .ToArray();
        }

        return sourceResults
            .SelectMany(results => results)
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(package => NuGetVersion.Parse(package.Version)).First())
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void InitializeCredentialService()
    {
        if (s_credentialServiceInitialized)
        {
            DefaultCredentialServiceUtility.UpdateCredentialServiceDelegatingLogger(_nuGetLogger);
            return;
        }

        lock (s_credentialServiceLock)
        {
            if (!s_credentialServiceInitialized)
            {
                DefaultCredentialServiceUtility.SetupDefaultCredentialService(_nuGetLogger, nonInteractive: true);
                s_credentialServiceInitialized = true;
            }
            else
            {
                DefaultCredentialServiceUtility.UpdateCredentialServiceDelegatingLogger(_nuGetLogger);
            }
        }
    }

    private async Task<Dictionary<PackageIdentity, SourcePackageDependencyInfo>> ResolveDependencyCandidatesAsync(
        IReadOnlyList<(string Id, VersionRange Range)> rootRequirements,
        IReadOnlyList<SourceRepository> repositories,
        PackageSourceMapping packageSourceMapping,
        IReadOnlyList<string> explicitSources,
        NuGetFramework targetFramework,
        SourceCacheContext cacheContext,
        CancellationToken cancellationToken)
    {
        var availablePackages = new Dictionary<PackageIdentity, SourcePackageDependencyInfo>(
            PackageIdentity.Comparer);
        var pendingRanges = new Queue<(string Id, VersionRange Range)>(rootRequirements);
        var processedRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pendingRanges.Count > 0)
        {
            var (packageId, versionRange) = pendingRanges.Dequeue();
            var rangeKey = $"{packageId}|{versionRange.ToNormalizedString()}";
            if (!processedRanges.Add(rangeKey))
            {
                continue;
            }

            var candidates = new List<SourcePackageDependencyInfo>();
            var candidateRepositories = GetMappedRepositories(
                packageId,
                repositories,
                packageSourceMapping,
                explicitSources);
            foreach (var repository in candidateRepositories)
            {
                var dependencyResource = await repository
                    .GetResourceAsync<DependencyInfoResource>(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"NuGet source '{repository.PackageSource.Source}' does not support dependency resolution.");
                var sourceCandidates = await dependencyResource.ResolvePackages(
                    packageId,
                    targetFramework,
                    cacheContext,
                    _nuGetLogger,
                    cancellationToken).ConfigureAwait(false);
                var matchingCandidates = sourceCandidates
                    .Where(candidate => versionRange.Satisfies(candidate.Version))
                    .ToArray();
                candidates.AddRange(matchingCandidates);

                // An exact version from an earlier source is definitive. Avoid probing fallback
                // sources, which also keeps local-feed restores independent of network access.
                if (matchingCandidates.Length > 0 &&
                    versionRange.MinVersion == versionRange.MaxVersion &&
                    versionRange.IsMinInclusive &&
                    versionRange.IsMaxInclusive)
                {
                    break;
                }
            }

            var distinctCandidates = candidates
                .GroupBy(item => item.Version)
                .Select(group => group.First())
                .OrderBy(item => item.Version)
                .ToArray();
            if (distinctCandidates.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve NuGet package '{packageId}' in version range '{versionRange}'.");
            }

            // PackageResolver needs every candidate version to reconcile ranges introduced
            // by different parents; retaining only the first match can create false conflicts.
            foreach (var candidate in distinctCandidates)
            {
                if (!availablePackages.TryAdd(candidate, candidate))
                {
                    continue;
                }

                foreach (var dependency in candidate.Dependencies)
                {
                    pendingRanges.Enqueue((dependency.Id, dependency.VersionRange));
                }
            }
        }

        return availablePackages;
    }

    private static IReadOnlyList<SourceRepository> GetMappedRepositories(
        string packageId,
        IReadOnlyList<SourceRepository> repositories,
        PackageSourceMapping packageSourceMapping,
        IReadOnlyList<string> explicitSources)
    {
        if (!packageSourceMapping.IsEnabled)
        {
            return repositories;
        }

        var mappedSourceNames = packageSourceMapping.GetConfiguredPackageSources(packageId);
        if (mappedSourceNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"NuGet package source mapping has no matching source for package '{packageId}'.");
        }

        var mappedSources = mappedSourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedRepositories = repositories
            .Where(repository => mappedSources.Contains(repository.PackageSource.Name))
            .ToArray();
        if (mappedRepositories.Length > 0)
        {
            return mappedRepositories;
        }

        // A temporary config can clear inherited package sources while inherited mappings remain.
        // In that case, use the sources explicitly selected by the CLI instead of failing because
        // none of the mapped source names are available.
        var explicitSourceSet = explicitSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitRepositories = repositories
            .Where(repository => explicitSourceSet.Contains(repository.PackageSource.Source))
            .ToArray();
        if (explicitRepositories.Length > 0)
        {
            return explicitRepositories;
        }

        throw new InvalidOperationException(
            $"NuGet package source mapping for package '{packageId}' refers to unavailable source(s): {string.Join(", ", mappedSourceNames)}.");
    }

    private async Task<string> DownloadPackageAsync(
        SourcePackageDependencyInfo package,
        string globalPackagesFolder,
        VersionFolderPathResolver pathResolver,
        PackageExtractionContext extractionContext,
        SourceCacheContext cacheContext,
        CancellationToken cancellationToken)
    {
        var source = package.Source
            ?? throw new InvalidOperationException($"NuGet package '{package.Id}' did not identify its source.");
        var downloadResource = await source
            .GetResourceAsync<DownloadResource>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"NuGet source '{source.PackageSource.Source}' does not support package downloads.");
        using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
            package,
            new PackageDownloadContext(cacheContext),
            globalPackagesFolder,
            _nuGetLogger,
            cancellationToken).ConfigureAwait(false);

        if (downloadResult.Status != DownloadResourceResultStatus.Available ||
            downloadResult.PackageStream is null)
        {
            throw new InvalidOperationException(
                $"Unable to download NuGet package '{package.Id}' version '{package.Version}' from '{source.PackageSource.Source}'.");
        }

        var installed = await PackageExtractor.InstallFromSourceAsync(
            source.PackageSource.Source,
            package,
            async destination =>
            {
                downloadResult.PackageStream.Position = 0;
                await downloadResult.PackageStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            },
            pathResolver,
            extractionContext,
            cancellationToken).ConfigureAwait(false);
        if (!installed)
        {
            throw new InvalidOperationException(
                $"NuGet package '{package.Id}' version '{package.Version}' could not be installed.");
        }

        return pathResolver.GetInstallPath(package.Id, package.Version)
            ?? throw new InvalidOperationException(
                $"NuGet package '{package.Id}' version '{package.Version}' was downloaded but not installed.");
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> SearchSourceAsync(
        PackageSource source,
        string query,
        global::NuGet.Protocol.Core.Types.SearchFilter filter,
        int take,
        CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
        if (searchResource is null)
        {
            return [];
        }

        var packages = new List<NuGetSearchResult>();
        var skip = 0;
        while (true)
        {
            var results = (await searchResource.SearchAsync(
                query,
                filter,
                skip,
                take,
                _nuGetLogger,
                cancellationToken).ConfigureAwait(false)).ToArray();

            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = await result.GetVersionsAsync().ConfigureAwait(false);
                packages.Add(new NuGetSearchResult(
                    result.Identity.Id,
                    result.Identity.Version.ToString(),
                    source.Source,
                    versions?.Select(version => version.Version.ToString()).ToArray() ?? []));
            }

            if (results.Length < take)
            {
                break;
            }

            skip += take;
        }

        return packages;
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> GetPackageMetadataAsync(
        PackageSource source,
        string packageId,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
        if (metadataResource is null)
        {
            return [];
        }

        using var cacheContext = new SourceCacheContext
        {
            NoCache = !useCache,
            DirectDownload = !useCache
        };
        var metadata = (await metadataResource.GetMetadataAsync(
            packageId,
            prerelease,
            includeUnlisted: false,
            cacheContext,
            _nuGetLogger,
            cancellationToken).ConfigureAwait(false)).ToArray();
        if (metadata.Length == 0)
        {
            return [];
        }

        var latest = metadata
            .OrderByDescending(package => package.Identity.Version)
            .First();
        return
        [
            new NuGetSearchResult(
                latest.Identity.Id,
                latest.Identity.Version.ToString(),
                source.Source,
                metadata.Select(package => package.Identity.Version.ToString()).ToArray())
        ];
    }

    private static IEnumerable<PackageAsset> ResolvePackageAssets(
        RestoredNuGetPackage package,
        NuGetFramework targetFramework,
        IReadOnlyList<string> runtimeIdentifiers)
    {
        var files = Directory.EnumerateFiles(package.InstallPath, "*", SearchOption.AllDirectories)
            .Select(path => new PackageFile(path, NormalizeRelativePath(Path.GetRelativePath(package.InstallPath, path))))
            .ToArray();
        var frameworkReducer = new FrameworkReducer();
        var baseGroup = FindNearestFrameworkGroup(files, "lib", targetFramework, frameworkReducer);
        var runtimeGroup = FindRuntimeGroup(files, targetFramework, runtimeIdentifiers, frameworkReducer);
        var runtimeOverrides = runtimeGroup
            .Where(file => file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase);
        var baseAssemblyNames = baseGroup
            .Where(file => file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetFileName(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in baseGroup)
        {
            if (!file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file.Path).Equals("_._", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var culture = GetResourceCulture(file.RelativePath);
            if (culture is null && runtimeOverrides.TryGetValue(Path.GetFileName(file.Path), out var runtimePath))
            {
                yield return new PackageAsset(package.Id, package.Version, runtimePath.Path, IsManagedAssembly: true, IsNativeLibrary: false, Culture: null);
            }
            else
            {
                yield return new PackageAsset(package.Id, package.Version, file.Path, IsManagedAssembly: true, IsNativeLibrary: false, culture);
            }
        }

        foreach (var runtimeFile in runtimeGroup)
        {
            if (runtimeFile.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !baseAssemblyNames.Contains(Path.GetFileName(runtimeFile.Path)))
            {
                yield return new PackageAsset(package.Id, package.Version, runtimeFile.Path, IsManagedAssembly: true, IsNativeLibrary: false, Culture: null);
            }
        }

        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            var nativePrefix = $"runtimes/{runtimeIdentifier}/native/";
            var nativeFiles = files.Where(file =>
                file.RelativePath.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nativeFiles.Length == 0)
            {
                continue;
            }

            foreach (var nativeFile in nativeFiles)
            {
                yield return new PackageAsset(package.Id, package.Version, nativeFile.Path, IsManagedAssembly: false, IsNativeLibrary: true, Culture: null);
            }

            break;
        }
    }

    private static IReadOnlyList<PackageFile> FindNearestFrameworkGroup(
        IReadOnlyList<PackageFile> files,
        string root,
        NuGetFramework targetFramework,
        FrameworkReducer frameworkReducer)
    {
        var prefix = $"{root}/";
        var groups = files
            .Where(file => file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(file => (File: file, Segments: file.RelativePath.Split('/')))
            .Where(item => item.Segments.Length >= 3)
            .GroupBy(item => NuGetFramework.ParseFolder(item.Segments[1]))
            .ToArray();
        var nearestFramework = frameworkReducer.GetNearest(targetFramework, groups.Select(group => group.Key));
        return nearestFramework is null
            ? []
            : groups.First(group => group.Key.Equals(nearestFramework)).Select(item => item.File).ToArray();
    }

    private static IReadOnlyList<PackageFile> FindRuntimeGroup(
        IReadOnlyList<PackageFile> files,
        NuGetFramework targetFramework,
        IReadOnlyList<string> runtimeIdentifiers,
        FrameworkReducer frameworkReducer)
    {
        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            var prefix = $"runtimes/{runtimeIdentifier}/lib/";
            var groups = files
                .Where(file => file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(file => (File: file, Segments: file.RelativePath.Split('/')))
                .Where(item => item.Segments.Length >= 5)
                .GroupBy(item => NuGetFramework.ParseFolder(item.Segments[3]))
                .ToArray();
            var nearestFramework = frameworkReducer.GetNearest(targetFramework, groups.Select(group => group.Key));
            if (nearestFramework is not null)
            {
                return groups.First(group => group.Key.Equals(nearestFramework)).Select(item => item.File).ToArray();
            }
        }

        return [];
    }

    private static IReadOnlyList<string> GetRuntimeIdentifiers(string? runtimeIdentifier)
    {
        var effectiveRuntimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
            : runtimeIdentifier;
        var runtimeIdentifiers = new List<string> { effectiveRuntimeIdentifier };
        var separatorIndex = effectiveRuntimeIdentifier.LastIndexOf('-');
        var platform = separatorIndex > 0
            ? effectiveRuntimeIdentifier[..separatorIndex]
            : effectiveRuntimeIdentifier;

        if (!runtimeIdentifiers.Contains(platform, StringComparer.OrdinalIgnoreCase))
        {
            runtimeIdentifiers.Add(platform);
        }

        if (platform.StartsWith("linux", StringComparison.OrdinalIgnoreCase))
        {
            runtimeIdentifiers.Add("linux");
            runtimeIdentifiers.Add("unix");
        }
        else if (platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
        {
            runtimeIdentifiers.Add("osx");
            runtimeIdentifiers.Add("unix");
        }
        else if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            runtimeIdentifiers.Add("win");
        }

        runtimeIdentifiers.Add("any");
        return runtimeIdentifiers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? GetResourceCulture(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length >= 4 &&
            segments[^1].EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
                ? segments[^2]
                : null;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static ISettings LoadSettings(string? nugetConfigPath, string workingDirectory)
    {
        if (!string.IsNullOrEmpty(nugetConfigPath))
        {
            return Settings.LoadSpecificSettings(
                Path.GetDirectoryName(nugetConfigPath)!,
                Path.GetFileName(nugetConfigPath));
        }

        return Settings.LoadDefaultSettings(workingDirectory);
    }

    private static List<PackageSource> LoadPackageSources(
        ISettings settings,
        IReadOnlyList<string> explicitSources)
    {
        if (explicitSources.Count > 0)
        {
            return explicitSources.Select(source => new PackageSource(source)).ToList();
        }

        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Where(source => source.IsEnabled)
            .ToList();

        if (sources.Count == 0)
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
        }

        return sources;
    }

    private static List<PackageSource> ResolvePackageSources(
        ISettings settings,
        IReadOnlyList<string> cliSources)
    {
        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Where(source => source.IsEnabled)
            .ToList();

        foreach (var cliSource in cliSources)
        {
            if (!sources.Any(source => source.Source.Equals(cliSource, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new PackageSource(cliSource));
            }
        }

        if (!sources.Any(source => source.Source.Equals(NuGetOrgUrl, StringComparison.OrdinalIgnoreCase)))
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
        }

        return sources;
    }

    private sealed record PackageFile(string Path, string RelativePath);

    private sealed record PackageAsset(
        string PackageId,
        string PackageVersion,
        string Path,
        bool IsManagedAssembly,
        bool IsNativeLibrary,
        string? Culture);

    private sealed class NuGetLogger(ILogger logger) : INuGetLogger
    {
        public void Log(NuGetLogLevel level, string data) => logger.Log(MapLogLevel(level), "{Message}", data);
        public void Log(NuGetLogMessage message) => Log(message.Level, message.Message);

        public Task LogAsync(NuGetLogLevel level, string data)
        {
            Log(level, data);
            return Task.CompletedTask;
        }

        public Task LogAsync(NuGetLogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }

        public void LogDebug(string data) => Log(NuGetLogLevel.Debug, data);
        public void LogError(string data) => Log(NuGetLogLevel.Error, data);
        public void LogInformation(string data) => Log(NuGetLogLevel.Information, data);
        public void LogInformationSummary(string data) => Log(NuGetLogLevel.Information, data);
        public void LogMinimal(string data) => Log(NuGetLogLevel.Minimal, data);
        public void LogVerbose(string data) => Log(NuGetLogLevel.Verbose, data);
        public void LogWarning(string data) => Log(NuGetLogLevel.Warning, data);

        private static LogLevel MapLogLevel(NuGetLogLevel level) => level switch
        {
            NuGetLogLevel.Debug or NuGetLogLevel.Verbose => LogLevel.Debug,
            NuGetLogLevel.Information or NuGetLogLevel.Minimal => LogLevel.Information,
            NuGetLogLevel.Warning => LogLevel.Warning,
            NuGetLogLevel.Error => LogLevel.Error,
            _ => LogLevel.None
        };
    }

    private static class NativeAotNuGetTrustStore
    {
        private static readonly object s_lock = new();
        private static bool s_initialized;

        public static void Initialize(INuGetLogger logger, IEnvironment environment)
        {
            if (s_initialized ||
                !environment.IsLinux() ||
                !bool.TryParse(environment.GetEnvironmentVariable(
                    NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification), out var enabled) ||
                !enabled)
            {
                return;
            }

            lock (s_lock)
            {
                if (s_initialized)
                {
                    return;
                }

                var previousSdkRoot = AppContext.GetData("Microsoft.DotNet.Sdk.Root");
                var rootDirectory = Directory.CreateTempSubdirectory("aspire-nuget-trust-");
                try
                {
                    var trustedRootsDirectory = Directory.CreateDirectory(
                        Path.Combine(rootDirectory.FullName, "trustedroots"));
                    WriteResource("codesignctl.pem", trustedRootsDirectory.FullName);
                    WriteResource("timestampctl.pem", trustedRootsDirectory.FullName);

                    // NuGet resolves its fallback trust bundles under Microsoft.DotNet.Sdk.Root.
                    // Point it at the securely extracted embedded SDK bundles only while the factories initialize.
                    AppContext.SetData("Microsoft.DotNet.Sdk.Root", rootDirectory.FullName);
                    X509TrustStore.InitializeForDotNetSdk(logger);
                    s_initialized = true;
                }
                finally
                {
                    AppContext.SetData("Microsoft.DotNet.Sdk.Root", previousSdkRoot);
                    rootDirectory.Delete(recursive: true);
                }
            }
        }

        private static void WriteResource(string resourceName, string destinationDirectory)
        {
            using var resourceStream = typeof(NuGetClient).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded NuGet trust root resource '{resourceName}' was not found.");
            using var fileStream = File.Create(Path.Combine(destinationDirectory, resourceName));
            resourceStream.CopyTo(fileStream);
        }
    }
}
