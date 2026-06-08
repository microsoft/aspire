// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Packaging;

internal class PackageChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, IFeatures features, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null, ILogger? logger = null)
{
    // Threaded so the local-folder integration listing can honor the same
    // ShowDeprecatedPackages flag that NuGetPackageCache honors on the feed-based path.
    // Without this, flipping the flag silently has no effect on local hive / PR hive listings
    // (https://github.com/microsoft/aspire/issues — divergence between two paths through the same intent).
    private readonly IFeatures _features = features;

    private const string GuestAppHostSdkPackageId = "Aspire.Hosting";

    public string Name { get; } = name;
    public PackageChannelQuality Quality { get; } = quality;
    public PackageMapping[]? Mappings { get; } = mappings;
    public PackageChannelType Type { get; } = mappings is null ? PackageChannelType.Implicit : PackageChannelType.Explicit;
    public bool ConfigureGlobalPackagesFolder { get; } = configureGlobalPackagesFolder;
    public string? CliDownloadBaseUrl { get; } = cliDownloadBaseUrl;
    public string? PinnedVersion { get; } = pinnedVersion;

    public string SourceDetails { get; } = ComputeSourceDetails(mappings);

    public bool ShouldPersistChannelName() =>
        Type is PackageChannelType.Explicit && !string.Equals(Name, PackageChannelNames.Stable, StringComparisons.ChannelName);

    private static string ComputeSourceDetails(PackageMapping[]? mappings)
    {
        if (mappings is null)
        {
            return PackagingStrings.BasedOnNuGetConfig;
        }

        var aspireMapping = mappings.FirstOrDefault(m => m.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase));
        var allPackagesMapping = mappings.FirstOrDefault(m => m.PackageFilter == PackageMapping.AllPackages);

        if (aspireMapping is not null)
        {
            return aspireMapping.Source;
        }
        else
        {
            return allPackagesMapping?.Source ?? PackagingStrings.BasedOnNuGetConfig;
        }
    }

    public async Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        if (PinnedVersion is not null)
        {
            return [new NuGetPackage { Id = "Aspire.ProjectTemplates", Version = PinnedVersion, Source = SourceDetails }];
        }

        var tasks = new List<Task<IEnumerable<NuGetPackage>>>();

        using var tempNuGetConfig = Type is PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(Mappings!) : null;

        if (Quality is PackageChannelQuality.Stable || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetTemplatePackagesAsync(workingDirectory, false, tempNuGetConfig?.ConfigFile, cancellationToken));
        }

        if (Quality is PackageChannelQuality.Prerelease || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetTemplatePackagesAsync(workingDirectory, true, tempNuGetConfig?.ConfigFile, cancellationToken));
        }

        var packageResults = await Task.WhenAll(tasks);

        var packages = packageResults
            .SelectMany(p => p)
            .DistinctBy(p => $"{p.Id}-{p.Version}");

        // When doing a `dotnet package search` the results may include stable packages even when searching for
        // prerelease packages. Keep the current CLI/SDK version so shipped CLIs can resolve their
        // matching template package from daily/staging feeds, then filter out the remaining noise.
        var currentCliVersion = VersionHelper.GetDefaultSdkVersion();
        var filteredPackages = packages.Where(p => new { SemVer = SemVersion.Parse(p.Version), Quality = Quality } switch
        {
            { Quality: PackageChannelQuality.Both } => true,
            { Quality: PackageChannelQuality.Stable, SemVer: { IsPrerelease: false } } => true,
            { Quality: PackageChannelQuality.Prerelease, SemVer: { IsPrerelease: true } } => true,
            { Quality: PackageChannelQuality.Prerelease, SemVer: { IsPrerelease: false } } when string.Equals(p.Version, currentCliVersion, StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        });

        return filteredPackages;
    }

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var localPackageSource = GetLocalAspirePackageSource();
        if (localPackageSource is not null)
        {
            return GetIntegrationPackagesFromLocalPackageSource(localPackageSource, cancellationToken);
        }

        var tasks = new List<Task<IEnumerable<NuGetPackage>>>();

        using var tempNuGetConfig = Type is PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(Mappings!) : null;

        if (Quality is PackageChannelQuality.Stable || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetIntegrationPackagesAsync(workingDirectory, false, tempNuGetConfig?.ConfigFile, cancellationToken));
        }

        if (Quality is PackageChannelQuality.Prerelease || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetIntegrationPackagesAsync(workingDirectory, true, tempNuGetConfig?.ConfigFile, cancellationToken));
        }

        var packageResults = await Task.WhenAll(tasks);

        var packages = packageResults
            .SelectMany(p => p)
            .DistinctBy(p => $"{p.Id}-{p.Version}");

        // When doing a `dotnet package search` the results may include stable packages even when searching for
        // prerelease packages. This filters out this noise.
        var filteredPackages = packages.Where(p => new { SemVer = SemVersion.Parse(p.Version), Quality = Quality } switch
        {
            { Quality: PackageChannelQuality.Both } => true,
            { Quality: PackageChannelQuality.Stable, SemVer: { IsPrerelease: false } } => true,
            { Quality: PackageChannelQuality.Prerelease, SemVer: { IsPrerelease: true } } => true,
            _ => false
        });

        // When pinned to a specific version, override the version on each discovered package
        // so the correct version gets installed regardless of what the feed reports as latest.
        if (PinnedVersion is not null)
        {
            return filteredPackages.Select(p => new NuGetPackage { Id = p.Id, Version = PinnedVersion, Source = p.Source });
        }

        return filteredPackages;
    }

    private DirectoryInfo? GetLocalAspirePackageSource()
    {
        if (Type is not PackageChannelType.Explicit || Mappings is null)
        {
            return null;
        }

        foreach (var mapping in Mappings)
        {
            if (IsScopedAspireMapping(mapping) && Directory.Exists(mapping.Source))
            {
                return new DirectoryInfo(mapping.Source);
            }
        }

        return null;
    }

    private IEnumerable<NuGetPackage> GetIntegrationPackagesFromLocalPackageSource(DirectoryInfo packageSource, CancellationToken cancellationToken)
    {
        // Mirror NuGetPackageCache.GetIntegrationPackagesAsync: a user who flipped
        // ShowDeprecatedPackages to see deprecated packages on stable/staging/daily
        // must also see them on local-hive / PR-hive listings. Previously the deprecation
        // check was hardcoded into IsIntegrationPackageId and silently dropped them here.
        var showDeprecatedPackages = _features.IsFeatureEnabled(KnownFeatures.ShowDeprecatedPackages, defaultValue: false);

        var packageMetadata = packageSource
            .EnumerateFiles("*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return GetPackageFileMetadata(file.FullName);
            })
            .OfType<PackageFileMetadata>()
            .Where(metadata => IsIntegrationPackageId(metadata.PackageId))
            .Where(metadata => showDeprecatedPackages || !DeprecatedPackages.IsDeprecated(metadata.PackageId))
            .Where(IsAllowedByQuality);

        if (PinnedVersion is not null)
        {
            packageMetadata = packageMetadata
                .Where(metadata => string.Equals(metadata.Version.ToString(), PinnedVersion, StringComparison.OrdinalIgnoreCase));
        }

        var source = PathNormalizer.NormalizePathForStorage(packageSource.FullName);

        return packageMetadata
            .GroupBy(metadata => metadata.PackageId, StringComparers.NuGetPackageId)
            .Select(group => group.OrderByDescending(metadata => metadata.Version, SemVersion.PrecedenceComparer).First())
            .OrderBy(metadata => metadata.PackageId, StringComparers.NuGetPackageId)
            .Select(metadata => new NuGetPackage { Id = metadata.PackageId, Version = PinnedVersion ?? metadata.Version.ToString(), Source = source })
            .ToArray();

        bool IsAllowedByQuality(PackageFileMetadata metadata) => new { metadata.Version, Quality } switch
        {
            { Quality: PackageChannelQuality.Both } => true,
            { Quality: PackageChannelQuality.Stable, Version: { IsPrerelease: false } } => true,
            { Quality: PackageChannelQuality.Prerelease, Version: { IsPrerelease: true } } => true,
            _ => false
        };
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackagesAsync(string packageId, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        if (PinnedVersion is not null)
        {
            return [new NuGetPackage { Id = packageId, Version = PinnedVersion, Source = SourceDetails }];
        }

        var tasks = new List<Task<IEnumerable<NuGetPackage>>>();

        using var tempNuGetConfig = Type is PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(Mappings!) : null;

        if (Quality is PackageChannelQuality.Stable || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetPackagesAsync(
                workingDirectory: workingDirectory,
                packageId: packageId,
                filter: id => id.Equals(packageId, StringComparisons.NuGetPackageId),
                prerelease: false,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken));
        }

        if (Quality is PackageChannelQuality.Prerelease || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetPackagesAsync(
                workingDirectory: workingDirectory,
                packageId: packageId,
                filter: id => id.Equals(packageId, StringComparisons.NuGetPackageId),
                prerelease: true,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken));
        }

        var packageResults = await Task.WhenAll(tasks);

        var packages = packageResults
            .SelectMany(p => p)
            .DistinctBy(p => $"{p.Id}-{p.Version}");

        // In the event that we have no stable packages we fallback to
        // returning prerelease packages. Example a package that is currently
        // in preview (Aspire.Hosting.Docker circa 9.4).
        if (Quality is PackageChannelQuality.Stable && !packages.Any())
        {
            packages = await nuGetPackageCache.GetPackagesAsync(
                workingDirectory: workingDirectory,
                packageId: packageId,
                filter: id => id.Equals(packageId, StringComparisons.NuGetPackageId),
                prerelease: true,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken);

            return packages;
        }

        // When doing a `dotnet package search` the results may include stable packages even when searching for
        // prerelease packages. This filters out this noise.
        var filteredPackages = packages.Where(p => new { SemVer = SemVersion.Parse(p.Version), Quality = Quality } switch
        {
            { Quality: PackageChannelQuality.Both } => true,
            { Quality: PackageChannelQuality.Stable, SemVer: { IsPrerelease: false } } => true,
            { Quality: PackageChannelQuality.Prerelease, SemVer: { IsPrerelease: true } } => true,
            _ => false
        });

        return filteredPackages;
    }

    public async Task<NuGetPackage?> GetLatestGuestAppHostSdkPackageAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        // Guest AppHost sdk.version resolves to the base Aspire.Hosting package because
        // the managed server restores that package to evaluate and generate the AppHost.
        var packages = await GetPackagesAsync(GuestAppHostSdkPackageId, workingDirectory, cancellationToken);

        NuGetPackage? latestPackage = null;
        SemVersion? latestVersion = null;
        foreach (var package in packages)
        {
            if (!SemVersion.TryParse(package.Version, SemVersionStyles.Strict, out var version))
            {
                continue;
            }

            if (latestVersion is null || SemVersion.PrecedenceComparer.Compare(version, latestVersion) > 0)
            {
                latestPackage = package;
                latestVersion = version;
            }
        }

        return latestPackage;
    }

    public async Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(string packageId, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<IEnumerable<NuGetPackage>>>();

        using var tempNuGetConfig = Type is PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(Mappings!) : null;

        if (Quality is PackageChannelQuality.Stable || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetPackageVersionsAsync(
                workingDirectory: workingDirectory,
                exactPackageId: packageId,
                prerelease: false,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken));
        }

        if (Quality is PackageChannelQuality.Prerelease || Quality is PackageChannelQuality.Both)
        {
            tasks.Add(nuGetPackageCache.GetPackageVersionsAsync(
                workingDirectory: workingDirectory,
                exactPackageId: packageId,
                prerelease: true,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken));
        }

        var packageResults = await Task.WhenAll(tasks);

        var packages = packageResults
            .SelectMany(p => p)
            .DistinctBy(p => $"{p.Id}-{p.Version}");

        // In the event that we have no stable packages we fallback to
        // returning prerelease packages. Example a package that is currently
        // in preview (Aspire.Hosting.Docker circa 9.4).
        if (Quality is PackageChannelQuality.Stable && !packages.Any())
        {
            packages = await nuGetPackageCache.GetPackageVersionsAsync(
                workingDirectory: workingDirectory,
                exactPackageId: packageId,
                prerelease: true,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken);

            return packages;
        }

        // When doing a `dotnet package search` the results may include stable packages even when searching for
        // prerelease packages. This filters out this noise.
        var filteredPackages = packages.Where(p => new { SemVer = SemVersion.Parse(p.Version), Quality = Quality } switch
        {
            { Quality: PackageChannelQuality.Both } => true,
            { Quality: PackageChannelQuality.Stable, SemVer: { IsPrerelease: false } } => true,
            { Quality: PackageChannelQuality.Prerelease, SemVer: { IsPrerelease: true } } => true,
            _ => false
        });

        return filteredPackages;
    }

    public PackageChannel CreateScopedChannelForPackage(string packageId)
    {
        return CreateScopedChannelForPackages([packageId]);
    }

    public PackageChannel CreateScopedChannelForPackages(IEnumerable<string> packageIds)
    {
        ArgumentNullException.ThrowIfNull(packageIds);

        var requestedPackageIds = packageIds
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
            .Distinct(StringComparers.NuGetPackageId)
            .ToArray();

        if (requestedPackageIds.Length == 0)
        {
            throw new ArgumentException("At least one package ID must be provided.", nameof(packageIds));
        }

        var mappings = Mappings;
        if (!VersionHelper.IsLocalBuildChannel(Name) || Type is not PackageChannelType.Explicit || mappings is not { Length: > 0 })
        {
            return this;
        }

        var scopedMappings = mappings
            .SelectMany(mapping => CreateScopedMappings(mapping, requestedPackageIds, logger))
            .ToArray();

        return new PackageChannel(Name, Quality, scopedMappings, nuGetPackageCache, _features, ConfigureGlobalPackagesFolder, CliDownloadBaseUrl, PinnedVersion, logger);
    }

    private static IEnumerable<PackageMapping> CreateScopedMappings(PackageMapping mapping, IReadOnlyCollection<string> packageIds, ILogger? logger)
    {
        if (!IsScopedAspireMapping(mapping))
        {
            yield return mapping;
            yield break;
        }

        var scopedPackageIds = GetScopedPackageIds(mapping.Source, packageIds, logger);

        foreach (var scopedPackageId in scopedPackageIds)
        {
            yield return new PackageMapping(scopedPackageId, mapping.Source);
        }
    }

    private static HashSet<string> GetScopedPackageIds(string source, IEnumerable<string> packageIds, ILogger? logger)
    {
        var resolvedPackageIds = new HashSet<string>(packageIds, StringComparers.NuGetPackageId);

        if (!Directory.Exists(source))
        {
            return resolvedPackageIds;
        }

        var packageFiles = Directory.EnumerateFiles(source, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(GetPackageFileMetadata)
            .OfType<PackageFileMetadata>()
            .GroupBy(metadata => metadata.PackageId, StringComparers.NuGetPackageId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(metadata => metadata.Version, SemVersion.PrecedenceComparer).First(),
                StringComparers.NuGetPackageId);

        var packagesToProcess = new Queue<string>(resolvedPackageIds);

        while (packagesToProcess.Count > 0)
        {
            var currentPackageId = packagesToProcess.Dequeue();
            if (!packageFiles.TryGetValue(currentPackageId, out var metadata))
            {
                continue;
            }

            foreach (var dependencyPackageId in GetDependencyPackageIds(metadata.PackageFilePath, logger))
            {
                if (packageFiles.ContainsKey(dependencyPackageId) && resolvedPackageIds.Add(dependencyPackageId))
                {
                    packagesToProcess.Enqueue(dependencyPackageId);
                }
            }
        }

        return resolvedPackageIds;
    }

    private static PackageFileMetadata? GetPackageFileMetadata(string packageFile)
    {
        var packageIdentity = TryGetPackageIdentityFromPackageFileName(packageFile);
        if (packageIdentity is null)
        {
            return null;
        }

        return new PackageFileMetadata(packageIdentity.Value.PackageId, packageIdentity.Value.Version, packageFile);
    }

    private static IEnumerable<string> GetDependencyPackageIds(string packageFile, ILogger? logger)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packageFile);
            var nuspecEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry is null)
            {
                return [];
            }

            using var stream = nuspecEntry.Open();
            var document = XDocument.Load(stream);
            return document
                .Descendants()
                .Where(element => element.Name.LocalName == "dependency")
                .Select(element => element.Attribute("id")?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparers.NuGetPackageId)
                .Cast<string>()
                .ToArray();
        }
        catch (IOException ex)
        {
            logger?.LogDebug(ex, "Failed to read package file '{PackageFile}' while resolving dependencies.", packageFile);
            return [];
        }
        catch (InvalidDataException ex)
        {
            logger?.LogDebug(ex, "Package file '{PackageFile}' contains invalid data.", packageFile);
            return [];
        }
        catch (System.Xml.XmlException ex)
        {
            logger?.LogDebug(ex, "Failed to parse nuspec in package file '{PackageFile}'.", packageFile);
            return [];
        }
    }

    private static (string PackageId, SemVersion Version)? TryGetPackageIdentityFromPackageFileName(string packageFile)
    {
        var packageFileName = Path.GetFileNameWithoutExtension(packageFile);
        if (string.IsNullOrWhiteSpace(packageFileName))
        {
            return null;
        }

        var separatorIndex = packageFileName.IndexOf('.');
        while (separatorIndex >= 0 && separatorIndex < packageFileName.Length - 1)
        {
            var versionCandidate = packageFileName[(separatorIndex + 1)..];
            if (SemVersion.TryParse(versionCandidate, SemVersionStyles.Strict, out var version))
            {
                return (packageFileName[..separatorIndex], version);
            }

            separatorIndex = packageFileName.IndexOf('.', separatorIndex + 1);
        }

        return null;
    }

    private readonly record struct PackageFileMetadata(string PackageId, SemVersion Version, string PackageFilePath);

    private static bool IsScopedAspireMapping(PackageMapping mapping)
    {
        return mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mapping.PackageFilter, PackageMapping.AllPackages, StringComparison.Ordinal);
    }

    private static bool IsIntegrationPackageId(string packageId)
    {
        // NuGet package IDs are case-insensitive, so prefix checks use OrdinalIgnoreCase
        // to stay consistent with StringComparers.NuGetPackageId used elsewhere in this
        // file. .nupkg files on disk normally carry the canonical casing, but matching
        // case-insensitively avoids silently dropping integrations whose file names were
        // produced with a non-canonical casing (e.g. a third-party hive build).
        //
        // This method classifies a package id by namespace only. The deprecation filter
        // is applied separately in GetIntegrationPackagesFromLocalPackageSource so it can
        // be gated on the ShowDeprecatedPackages feature flag, matching the feed-based
        // path in NuGetPackageCache.
        var isHostingOrCommunityToolkitNamespaced = packageId.StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparison.OrdinalIgnoreCase);

        var isExcluded = packageId.StartsWith("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Sdk", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Orchestration", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Testing", StringComparison.OrdinalIgnoreCase) ||
            packageId.StartsWith("Aspire.Hosting.Msi", StringComparison.OrdinalIgnoreCase);

        return isHostingOrCommunityToolkitNamespaced && !isExcluded;
    }

    public static PackageChannel CreateExplicitChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, IFeatures features, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null, ILogger? logger = null)
    {
        return new PackageChannel(name, quality, mappings, nuGetPackageCache, features, configureGlobalPackagesFolder, cliDownloadBaseUrl, pinnedVersion, logger);
    }

    public static PackageChannel CreateImplicitChannel(INuGetPackageCache nuGetPackageCache, IFeatures features, ILogger? logger = null)
    {
        // The reason that PackageChannelQuality.Both is because there are situations like
        // in community toolkit where there is a newer beta version available for a package
        // in the case of implicit feeds we want to be able to show that, along side the stable
        // version. Not really an issue for template selection though (unless we start allowing)
        // for broader templating options.
        return new PackageChannel("default", PackageChannelQuality.Both, null, nuGetPackageCache, features, logger: logger);
    }
}
