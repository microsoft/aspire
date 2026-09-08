// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

internal sealed record NuGetConfigSource(
    string Key,
    string Source,
    bool IsAmbient,
    bool IsEnabled);

internal sealed class TemporaryNuGetConfig : IDisposable
{
    private readonly FileInfo _configFile;
    private bool _disposed;

    private TemporaryNuGetConfig(FileInfo configFile, string cacheIdentity)
    {
        _configFile = configFile;
        CacheIdentity = cacheIdentity;
    }

    public FileInfo ConfigFile => _configFile;

    public string CacheIdentity { get; private set; }

    public static async Task<TemporaryNuGetConfig> CreateAsync(
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        string? globalPackagesFolderValue = null)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        try
        {
            var configFile = new FileInfo(Path.Combine(tempDirectory, "nuget.config"));
            await GenerateNuGetConfigAsync(mappings, configFile, includePackageSources: true, clearPackageSources: true).ConfigureAwait(false);
            if (configureGlobalPackagesFolder)
            {
                await AddGlobalPackagesFolderToConfigAsync(configFile, globalPackagesFolderValue).ConfigureAwait(false);
            }

            return new TemporaryNuGetConfig(configFile, await ComputeCacheIdentityAsync(configFile).ConfigureAwait(false));
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    public static async Task<TemporaryNuGetConfig> CreateRestoreOverlayAsync(
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        string? globalPackagesFolderValue = null,
        IReadOnlyList<NuGetConfigSource>? sources = null,
        IReadOnlyList<string>? disabledAmbientSourceKeys = null)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        try
        {
            var configFile = new FileInfo(Path.Combine(tempDirectory, "nuget.config"));
            await GenerateNuGetConfigAsync(
                mappings,
                configFile,
                includePackageSources: false,
                clearPackageSources: false,
                sources,
                disabledAmbientSourceKeys).ConfigureAwait(false);
            if (configureGlobalPackagesFolder)
            {
                await AddGlobalPackagesFolderToConfigAsync(configFile, globalPackagesFolderValue).ConfigureAwait(false);
            }

            return new TemporaryNuGetConfig(configFile, await ComputeCacheIdentityAsync(configFile).ConfigureAwait(false));
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    /// <summary>
    /// Generates a standalone NuGet.config file at the specified path with the given package mappings.
    /// </summary>
    public static Task GenerateAsync(PackageMapping[] mappings, string targetPath)
        => GenerateNuGetConfigAsync(
            mappings,
            new FileInfo(targetPath),
            includePackageSources: true,
            clearPackageSources: true);

    /// <summary>
    /// Generates a NuGet.config policy overlay for a generated restore project.
    /// </summary>
    public static async Task GenerateRestoreOverlayAsync(
        PackageMapping[] mappings,
        string targetPath,
        string? globalPackagesFolderValue,
        IReadOnlyList<NuGetConfigSource>? sources = null,
        IReadOnlyList<string>? disabledAmbientSourceKeys = null)
    {
        var configFile = new FileInfo(targetPath);
        await GenerateNuGetConfigAsync(
            mappings,
            configFile,
            includePackageSources: false,
            clearPackageSources: false,
            sources,
            disabledAmbientSourceKeys).ConfigureAwait(false);

        if (globalPackagesFolderValue is not null)
        {
            await AddGlobalPackagesFolderToConfigAsync(configFile, globalPackagesFolderValue).ConfigureAwait(false);
        }
    }

    private static async Task GenerateNuGetConfigAsync(
        PackageMapping[] mappings,
        FileInfo configFile,
        bool includePackageSources,
        bool clearPackageSources,
        IReadOnlyList<NuGetConfigSource>? configuredSources = null,
        IReadOnlyList<string>? disabledAmbientSourceKeys = null)
    {
        var distinctSources = mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer)
            .SelectMany((source, index) =>
            {
                var matchingSources = configuredSources?
                    .Where(configuredSource => PackageSourceIdentity.Comparer.Equals(
                        configuredSource.Source,
                        source))
                    .ToArray();
                return matchingSources is { Length: > 0 }
                    ? matchingSources
                    : [new NuGetConfigSource(
                        includePackageSources ? $"aspire-{index}" : source,
                        source,
                        IsAmbient: !includePackageSources,
                        IsEnabled: true)];
            })
            .DistinctBy(static source => source.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var fileStream = configFile.Create();
        await using var streamWriter = new StreamWriter(fileStream);
        await using var xmlWriter = XmlWriter.Create(streamWriter, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = Environment.NewLine,
            Encoding = Encoding.UTF8,
            Async = true
        });

        await xmlWriter.WriteStartDocumentAsync();
        await xmlWriter.WriteStartElementAsync(null, "configuration", null);

        if (includePackageSources || distinctSources.Any(static source => !source.IsAmbient))
        {
            await xmlWriter.WriteStartElementAsync(null, "packageSources", null);
            if (clearPackageSources)
            {
                await xmlWriter.WriteStartElementAsync(null, "clear", null);
                await xmlWriter.WriteEndElementAsync();
            }

            foreach (var sourceInfo in distinctSources)
            {
                if (!includePackageSources && sourceInfo.IsAmbient)
                {
                    continue;
                }

                await xmlWriter.WriteStartElementAsync(null, "add", null);
                await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceInfo.Key);
                await xmlWriter.WriteAttributeStringAsync(null, "value", null, sourceInfo.Source);
                await xmlWriter.WriteEndElementAsync();
            }

            await xmlWriter.WriteEndElementAsync();
        }

        var sourcesRequiringEnablement = distinctSources
            .Where(static source => source.IsAmbient)
            .GroupBy(static source => source.Source, PackageSourceIdentity.Comparer)
            .Where(static group => group.All(static source => !source.IsEnabled))
            .Select(static group => group.First())
            .ToArray();
        if (sourcesRequiringEnablement.Length > 0)
        {
            await xmlWriter.WriteStartElementAsync(null, "disabledPackageSources", null);
            await xmlWriter.WriteStartElementAsync(null, "clear", null);
            await xmlWriter.WriteEndElementAsync();

            var enabledSourceKeys = sourcesRequiringEnablement
                .Select(static source => source.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var disabledSourceKeys = (disabledAmbientSourceKeys ?? [])
                .Concat(configuredSources?
                    .Where(static source => source.IsAmbient && !source.IsEnabled)
                    .Select(static source => source.Key) ?? [])
                .Where(key => !enabledSourceKeys.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceKey in disabledSourceKeys)
            {
                await xmlWriter.WriteStartElementAsync(null, "add", null);
                await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceKey);
                await xmlWriter.WriteAttributeStringAsync(null, "value", null, "true");
                await xmlWriter.WriteEndElementAsync();
            }

            await xmlWriter.WriteEndElementAsync();
        }

        if (mappings.Length > 0)
        {
            await xmlWriter.WriteStartElementAsync(null, "packageSourceMapping", null);
            if (!includePackageSources)
            {
                await xmlWriter.WriteStartElementAsync(null, "clear", null);
                await xmlWriter.WriteEndElementAsync();
            }

            foreach (var sourceGroup in mappings.GroupBy(static mapping => mapping.Source, PackageSourceIdentity.Comparer))
            {
                var mappingsForSource = sourceGroup.ToArray();
                var matchingSources = distinctSources
                    .Where(source => PackageSourceIdentity.Comparer.Equals(source.Source, sourceGroup.Key))
                    .ToArray();
                if (matchingSources.All(static source => source.IsAmbient && !source.IsEnabled))
                {
                    matchingSources = [matchingSources[0]];
                }

                var sourceKeys = matchingSources
                    .SelectMany(source => includePackageSources ||
                        string.Equals(source.Key, source.Source, StringComparison.Ordinal)
                            ? [source.Key]
                            : new[] { source.Key, source.Source })
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var sourceKey in sourceKeys)
                {
                    await xmlWriter.WriteStartElementAsync(null, "packageSource", null);
                    await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceKey);

                    foreach (var mapping in mappingsForSource)
                    {
                        await xmlWriter.WriteStartElementAsync(null, "package", null);
                        await xmlWriter.WriteAttributeStringAsync(null, "pattern", null, mapping.PackageFilter);
                        await xmlWriter.WriteEndElementAsync();
                    }

                    await xmlWriter.WriteEndElementAsync();
                }
            }

            await xmlWriter.WriteEndElementAsync();
        }

        await xmlWriter.WriteEndElementAsync();
        await xmlWriter.WriteEndDocumentAsync();
    }

    private static async Task AddGlobalPackagesFolderToConfigAsync(FileInfo configFile, string? globalPackagesFolderValue)
    {
        var document = await LoadAsync(configFile).ConfigureAwait(false);
        var configuration = document.Root ?? new XElement("configuration");
        if (document.Root is null)
        {
            document.Add(configuration);
        }

        NuGetConfigMerger.AddGlobalPackagesFolderConfiguration(configuration, globalPackagesFolderValue);

        var content = document.Declaration is null
            ? document.ToString()
            : $"{document.Declaration}{Environment.NewLine}{document}";
        await File.WriteAllTextAsync(configFile.FullName, content).ConfigureAwait(false);
    }

    public async Task SetGlobalPackagesFolderAsync(string globalPackagesFolderValue)
    {
        await AddGlobalPackagesFolderToConfigAsync(_configFile, globalPackagesFolderValue).ConfigureAwait(false);
        CacheIdentity = await ComputeCacheIdentityAsync(_configFile).ConfigureAwait(false);
    }

    private static async Task<string> ComputeCacheIdentityAsync(FileInfo configFile)
    {
        var document = await LoadAsync(configFile).ConfigureAwait(false);
        document
            .Descendants("config")
            .Elements("add")
            .Where(static element => string.Equals(
                (string?)element.Attribute("key"),
                "globalPackagesFolder",
                StringComparison.OrdinalIgnoreCase))
            .Remove();
        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        return Convert.ToHexString(XxHash3.Hash(bytes));
    }

    private static async Task<XDocument> LoadAsync(FileInfo configFile)
    {
        await using var stream = new FileStream(
            configFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_configFile.Exists)
            {
                _configFile.Delete();
                _configFile.Directory?.Delete(recursive: true);
            }
        }
        catch
        {
            // Temporary configuration cleanup is best effort.
        }

        _disposed = true;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Preserve the original creation failure.
        }
    }
}
