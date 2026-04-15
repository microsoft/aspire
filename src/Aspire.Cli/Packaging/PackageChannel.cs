// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Semver;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Packaging;

internal class PackageChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null)
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

        // When doing a `dotnet package search` the the results may include stable packages even when searching for
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

        // When doing a `dotnet package search` the the results may include stable packages even when searching for
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
                filter: id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase),
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
                filter: id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase),
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
                filter: id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase),
                prerelease: true,
                nugetConfigFile: tempNuGetConfig?.ConfigFile,
                useCache: true, // Enable caching for package channel resolution
                cancellationToken: cancellationToken);

            return packages;
        }

        // When doing a `dotnet package search` the the results may include stable packages even when searching for
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
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var mappings = Mappings;
        if (!VersionHelper.IsPrChannel(Name) || Type is not PackageChannelType.Explicit || mappings is not { Length: > 0 })
        {
            return this;
        }

        var scopedMappings = mappings
            .SelectMany(mapping => CreateScopedMappings(mapping, packageId))
            .ToArray();

        return new PackageChannel(Name, Quality, scopedMappings, nuGetPackageCache, ConfigureGlobalPackagesFolder, CliDownloadBaseUrl, PinnedVersion);
    }

    private static IEnumerable<PackageMapping> CreateScopedMappings(PackageMapping mapping, string packageId)
    {
        if (!IsScopedAspireMapping(mapping))
        {
            yield return mapping;
            yield break;
        }

        var packageIds = GetScopedPackageIds(mapping.Source);
        if (packageIds.Count == 0)
        {
            yield return new PackageMapping(packageId, mapping.Source);
            yield break;
        }

        foreach (var scopedPackageId in packageIds)
        {
            yield return new PackageMapping(scopedPackageId, mapping.Source);
        }
    }

    private static HashSet<string> GetScopedPackageIds(string source)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(source))
        {
            return packageIds;
        }

        foreach (var packageFile in Directory.EnumerateFiles(source, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            if (TryGetPackageIdFromPackageFileName(packageFile) is { Length: > 0 } packageId)
            {
                packageIds.Add(packageId);
            }
        }

        return packageIds;
    }

    private static string? TryGetPackageIdFromPackageFileName(string packageFile)
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
            if (SemVersion.TryParse(versionCandidate, SemVersionStyles.Strict, out _))
            {
                return packageFileName[..separatorIndex];
            }

            separatorIndex = packageFileName.IndexOf('.', separatorIndex + 1);
        }

        return null;
    }

    private static bool IsScopedAspireMapping(PackageMapping mapping)
    {
        return mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mapping.PackageFilter, PackageMapping.AllPackages, StringComparison.Ordinal);
    }

    public static PackageChannel CreateExplicitChannel(string name, PackageChannelQuality quality, PackageMapping[]? mappings, INuGetPackageCache nuGetPackageCache, bool configureGlobalPackagesFolder = false, string? cliDownloadBaseUrl = null, string? pinnedVersion = null)
    {
        return new PackageChannel(name, quality, mappings, nuGetPackageCache, configureGlobalPackagesFolder, cliDownloadBaseUrl, pinnedVersion);
    }

    public static PackageChannel CreateImplicitChannel(INuGetPackageCache nuGetPackageCache)
    {
        // The reason that PackageChannelQuality.Both is because there are situations like
        // in community toolkit where there is a newer beta version available for a package
        // in the case of implicit feeds we want to be able to show that, along side the stable
        // version. Not really an issue for template selection though (unless we start allowing)
        // for broader templating options.
        return new PackageChannel("default", PackageChannelQuality.Both, null, nuGetPackageCache);
    }
}
