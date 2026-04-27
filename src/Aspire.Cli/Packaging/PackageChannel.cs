// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Packaging;

internal class PackageChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null, ILogger? logger = null)
{
    public string Name { get; } = name;
    public PackageChannelQuality Quality { get; } = quality;
    public PackageMapping[]? Mappings { get; } = mappings;
    public PackageChannelType Type { get; } = mappings is null ? PackageChannelType.Implicit : PackageChannelType.Explicit;
    public bool ConfigureGlobalPackagesFolder { get; } = configureGlobalPackagesFolder;
    public string? CliDownloadBaseUrl { get; } = cliDownloadBaseUrl;
    public string? PinnedVersion { get; } = pinnedVersion;

    public string SourceDetails { get; } = ComputeSourceDetails(mappings);

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

    public async Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
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

        return new PackageChannel(Name, Quality, scopedMappings, nuGetPackageCache, ConfigureGlobalPackagesFolder, CliDownloadBaseUrl, PinnedVersion, logger);
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

    public static PackageChannel CreateExplicitChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null, ILogger? logger = null)
    {
        return new PackageChannel(name, quality, mappings, nuGetPackageCache, configureGlobalPackagesFolder, cliDownloadBaseUrl, pinnedVersion, logger);
    }

    public static PackageChannel CreateImplicitChannel(INuGetPackageCache nuGetPackageCache, ILogger? logger = null)
    {
        // The reason that PackageChannelQuality.Both is because there are situations like
        // in community toolkit where there is a newer beta version available for a package
        // in the case of implicit feeds we want to be able to show that, along side the stable
        // version. Not really an issue for template selection though (unless we start allowing)
        // for broader templating options.
        return new PackageChannel("default", PackageChannelQuality.Both, null, nuGetPackageCache, logger: logger);
    }
}
