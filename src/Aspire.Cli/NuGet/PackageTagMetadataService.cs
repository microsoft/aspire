// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.NuGet;

internal interface IPackageTagMetadataService
{
    Task<bool> HasTagAsync(PackageChannel channel, DirectoryInfo workingDirectory, string packageId, string? packageVersion, string tag, CancellationToken cancellationToken);
}

internal sealed class PackageTagMetadataService(
    IDotNetCliRunner dotNetCliRunner,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<PackageTagMetadataService> logger) : IPackageTagMetadataService
{
    private static readonly TimeSpan s_cacheEntryLifetime = TimeSpan.FromHours(1);

    public async Task<bool> HasTagAsync(PackageChannel channel, DirectoryInfo workingDirectory, string packageId, string? packageVersion, string tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var versionToCheck = await ResolvePackageVersionAsync(channel, workingDirectory, packageId, packageVersion, cancellationToken);
        if (versionToCheck is null)
        {
            return false;
        }

        var cacheKey = $"PackageTags:{channel.Name}:{workingDirectory.FullName}:{packageId}:{versionToCheck}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = s_cacheEntryLifetime;

            foreach (var source in await GetCandidateSourcesAsync(channel, workingDirectory, packageId, cancellationToken))
            {
                var tags = await TryGetPackageTagsAsync(source, packageId, versionToCheck, cancellationToken);
                if (tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }

            return false;
        });
    }

    private static async Task<string?> ResolvePackageVersionAsync(PackageChannel channel, DirectoryInfo workingDirectory, string packageId, string? packageVersion, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(packageVersion) && packageVersion != "*")
        {
            return packageVersion;
        }

        var packages = await channel.GetPackageVersionsAsync(packageId, workingDirectory, cancellationToken);
        return packages
            .Where(static package => SemVersion.TryParse(package.Version, SemVersionStyles.Strict, out _))
            .OrderByDescending(static package => SemVersion.Parse(package.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
            .Select(static package => package.Version)
            .FirstOrDefault();
    }

    private async Task<string[]> GetCandidateSourcesAsync(PackageChannel channel, DirectoryInfo workingDirectory, string packageId, CancellationToken cancellationToken)
    {
        if (channel.Type is PackageChannelType.Explicit && channel.Mappings is { Length: > 0 })
        {
            return channel.Mappings
                .Where(mapping => IsRelevantMapping(mapping.PackageFilter, packageId))
                .Select(mapping => mapping.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return await GetImplicitChannelSourcesAsync(workingDirectory, cancellationToken);
    }

    private async Task<string[]> GetImplicitChannelSourcesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var cacheKey = $"PackageMetadataSources:Implicit:{workingDirectory.FullName}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = s_cacheEntryLifetime;

            var (exitCode, configPaths) = await dotNetCliRunner.GetNuGetConfigPathsAsync(workingDirectory, new ProcessInvocationOptions { SuppressLogging = true }, cancellationToken);
            if (exitCode != 0 || configPaths.Length == 0)
            {
                return [];
            }

            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var disabledSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var configPath in configPaths.Reverse())
            {
                ApplyNuGetConfig(configPath, sources, disabledSources);
            }

            return sources
                .Where(static source => !string.IsNullOrWhiteSpace(source.Value))
                .Where(source => !disabledSources.Contains(source.Key))
                .Select(static source => source.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }) ?? [];
    }

    private void ApplyNuGetConfig(string configPath, IDictionary<string, string> sources, ISet<string> disabledSources)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(configPath);
            var configuration = document.Root;
            if (configuration is null)
            {
                return;
            }

            var configDirectory = Path.GetDirectoryName(configPath) ?? string.Empty;
            ApplyPackageSources(configuration, configDirectory, sources);
            ApplyDisabledPackageSources(configuration, disabledSources);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            logger.LogDebug(ex, "Failed to read NuGet config '{ConfigPath}' while resolving package metadata sources.", configPath);
        }
    }

    private static void ApplyPackageSources(XElement configuration, string configDirectory, IDictionary<string, string> sources)
    {
        var packageSources = configuration.Element("packageSources");
        if (packageSources is null)
        {
            return;
        }

        if (packageSources.Elements("clear").Any())
        {
            sources.Clear();
        }

        foreach (var source in packageSources.Elements("add"))
        {
            var key = source.Attribute("key")?.Value;
            var value = source.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            sources[key] = NormalizeSourceValue(value, configDirectory);
        }
    }

    private static void ApplyDisabledPackageSources(XElement configuration, ISet<string> disabledSources)
    {
        var disabledPackageSources = configuration.Element("disabledPackageSources");
        if (disabledPackageSources is null)
        {
            return;
        }

        if (disabledPackageSources.Elements("clear").Any())
        {
            disabledSources.Clear();
        }

        foreach (var disabledSource in disabledPackageSources.Elements("add"))
        {
            var key = disabledSource.Attribute("key")?.Value;
            var value = disabledSource.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (bool.TryParse(value, out var disabled) && disabled)
            {
                disabledSources.Add(key);
            }
            else
            {
                disabledSources.Remove(key);
            }
        }
    }

    private static string NormalizeSourceValue(string sourceValue, string configDirectory)
    {
        if (UrlHelper.IsHttpUrl(sourceValue) || Path.IsPathRooted(sourceValue))
        {
            return sourceValue;
        }

        return Path.GetFullPath(Path.Combine(configDirectory, sourceValue));
    }

    private async Task<string[]?> TryGetPackageTagsAsync(string source, string packageId, string packageVersion, CancellationToken cancellationToken)
    {
        if (UrlHelper.IsHttpUrl(source))
        {
            return await TryGetRemotePackageTagsAsync(source, packageId, packageVersion, cancellationToken);
        }

        return TryGetLocalPackageTags(source, packageId, packageVersion);
    }

    private async Task<string[]?> TryGetRemotePackageTagsAsync(string source, string packageId, string packageVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            var registrationBaseUrl = await GetRegistrationBaseUrlAsync(client, source, packageId, cancellationToken);
            if (registrationBaseUrl is null)
            {
                return null;
            }

            var registrationIndex = new Uri($"{registrationBaseUrl.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json", UriKind.Absolute);
            var registrationDocument = await GetJsonDocumentAsync(client, registrationIndex, packageId, source, cancellationToken);
            if (registrationDocument is null)
            {
                return null;
            }

            return await FindTagsInRegistrationAsync(client, registrationDocument.RootElement, packageVersion, packageId, source, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Failed to load package metadata for '{PackageId}' from '{Source}'.", packageId, source);
            return null;
        }
    }

    private async Task<string?> GetRegistrationBaseUrlAsync(HttpClient client, string source, string packageId, CancellationToken cancellationToken)
    {
        var cacheKey = $"PackageMetadataRegistrationBaseUrl:{source}";
        var registrationBaseUrl = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = s_cacheEntryLifetime;

            using var serviceIndex = await GetJsonDocumentAsync(client, new Uri(source), packageId, source, cancellationToken);
            return serviceIndex is null
                ? string.Empty
                : GetRegistrationBaseUrl(serviceIndex.RootElement) ?? string.Empty;
        });

        return string.IsNullOrWhiteSpace(registrationBaseUrl) ? null : registrationBaseUrl;
    }

    private static string[]? TryGetLocalPackageTags(string source, string packageId, string packageVersion)
    {
        var packageFiles = GetCandidatePackageFiles(source);
        var packageFile = packageFiles.FirstOrDefault(path => MatchesPackageIdentity(path, packageId, packageVersion));
        if (packageFile is null)
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(packageFile);
            var nuspecEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry is null)
            {
                return null;
            }

            using var stream = nuspecEntry.Open();
            var document = XDocument.Load(stream);
            var tags = document
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "tags")
                ?.Value;

            return ParseTags(tags);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<string> GetCandidatePackageFiles(string source)
    {
        if (File.Exists(source) && string.Equals(Path.GetExtension(source), ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return [source];
        }

        if (Directory.Exists(source))
        {
            return Directory.EnumerateFiles(source, "*.nupkg", SearchOption.TopDirectoryOnly);
        }

        return [];
    }

    private static bool MatchesPackageIdentity(string packageFile, string packageId, string packageVersion)
    {
        var packageIdentity = TryGetPackageIdentityFromPackageFileName(packageFile);
        if (packageIdentity is null)
        {
            return false;
        }

        return string.Equals(packageIdentity.Value.PackageId, packageId, StringComparisons.NuGetPackageId) &&
            VersionsMatch(packageIdentity.Value.Version, packageVersion);
    }

    private static (string PackageId, string Version)? TryGetPackageIdentityFromPackageFileName(string packageFile)
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
                return (packageFileName[..separatorIndex], versionCandidate);
            }

            separatorIndex = packageFileName.IndexOf('.', separatorIndex + 1);
        }

        return null;
    }

    private static string? GetRegistrationBaseUrl(JsonElement serviceIndex)
    {
        if (!serviceIndex.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (!resource.TryGetProperty("@id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!resource.TryGetProperty("@type", out var type))
            {
                continue;
            }

            if (IsRegistrationBaseType(type))
            {
                return id.GetString();
            }
        }

        return null;
    }

    private static bool IsRegistrationBaseType(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString()?.StartsWith("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase) == true;
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in type.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String &&
                    element.GetString()?.StartsWith("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<string[]?> FindTagsInRegistrationAsync(HttpClient client, JsonElement registrationElement, string packageVersion, string packageId, string source, CancellationToken cancellationToken)
    {
        if (TryGetTagsFromCatalogEntry(registrationElement, packageVersion, out var tags))
        {
            return tags;
        }

        if (!registrationElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (TryGetTagsFromCatalogEntry(item, packageVersion, out tags))
            {
                return tags;
            }

            if (item.TryGetProperty("items", out _))
            {
                tags = await FindTagsInRegistrationAsync(client, item, packageVersion, packageId, source, cancellationToken);
                if (tags is not null)
                {
                    return tags;
                }
            }
            else if (item.TryGetProperty("@id", out var pageId) && pageId.ValueKind == JsonValueKind.String && Uri.TryCreate(pageId.GetString(), UriKind.Absolute, out var pageUri))
            {
                var pageDocument = await GetJsonDocumentAsync(client, pageUri, packageId, source, cancellationToken);
                if (pageDocument is null)
                {
                    continue;
                }

                tags = await FindTagsInRegistrationAsync(client, pageDocument.RootElement, packageVersion, packageId, source, cancellationToken);
                if (tags is not null)
                {
                    return tags;
                }
            }
        }

        return null;
    }

    private static bool TryGetTagsFromCatalogEntry(JsonElement element, string packageVersion, out string[]? tags)
    {
        tags = null;

        if (!element.TryGetProperty("catalogEntry", out var catalogEntry) || catalogEntry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!catalogEntry.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!VersionsMatch(version.GetString(), packageVersion))
        {
            return false;
        }

        tags = catalogEntry.TryGetProperty("tags", out var catalogTags)
            ? ParseTags(catalogTags)
            : [];

        return true;
    }

    private static string[] ParseTags(JsonElement tags)
    {
        return tags.ValueKind switch
        {
            JsonValueKind.String => ParseTags(tags.GetString()),
            JsonValueKind.Array => tags.EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.String)
                .Select(static element => element.GetString())
                .OfType<string>()
                .SelectMany(static value => ParseTags(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => []
        };
    }

    private static string[] ParseTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool VersionsMatch(string? actualVersion, string expectedVersion)
    {
        if (string.Equals(actualVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SemVersion.TryParse(actualVersion, SemVersionStyles.Strict, out var actual) &&
            SemVersion.TryParse(expectedVersion, SemVersionStyles.Strict, out var expected) &&
            SemVersion.PrecedenceComparer.Compare(actual, expected) == 0;
    }

    private static bool IsRelevantMapping(string packageFilter, string packageId)
    {
        return PackageMapping.MatchesPackageId(packageFilter, packageId);
    }

    private async Task<JsonDocument?> GetJsonDocumentAsync(HttpClient client, Uri uri, string packageId, string source, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogWarning(
                    "Package metadata for '{PackageId}' could not be verified from '{Source}' because the feed requires authentication. Authenticated feed metadata lookup is not supported.",
                    packageId,
                    source);
            }

            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
