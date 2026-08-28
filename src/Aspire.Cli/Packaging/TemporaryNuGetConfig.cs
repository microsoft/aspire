// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

internal sealed class TemporaryNuGetConfig : IDisposable
{
    private readonly FileInfo _configFile;
    private bool _disposed;

    private TemporaryNuGetConfig(FileInfo configFile, bool containsCredentialMaterial)
    {
        _configFile = configFile;
        ContainsCredentialMaterial = containsCredentialMaterial;
    }

    public FileInfo ConfigFile => _configFile;

    public bool ContainsCredentialMaterial { get; }

    public static async Task<TemporaryNuGetConfig> CreateAsync(
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        string? globalPackagesFolderValue = null)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        try
        {
            var tempFilePath = Path.Combine(tempDirectory, "nuget.config");
            var configFile = new FileInfo(tempFilePath);
            await GenerateNuGetConfigAsync(mappings, configFile);
            if (configureGlobalPackagesFolder)
            {
                await AddGlobalPackagesFolderToConfigAsync(configFile, globalPackagesFolderValue);
            }
            return new TemporaryNuGetConfig(
                configFile,
                mappings.Any(static mapping => PackageSourceOverrideMappings.HasCredentialMaterial(mapping.Source)));
        }
        catch
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures; surface the original exception instead.
            }
            throw;
        }
    }

    public static async Task<TemporaryNuGetConfig> CreateComposedAsync(
        IReadOnlyList<string> configPaths,
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        string? globalPackagesFolderValue = null,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        try
        {
            var configFile = new FileInfo(Path.Combine(tempDirectory, "nuget.config"));
            var document = await NuGetConfigComposer.ComposeAsync(configPaths, cancellationToken).ConfigureAwait(false);
            await SaveAsync(document, configFile, cancellationToken).ConfigureAwait(false);
            await NuGetConfigMerger.CreateOrUpdateAsync(
                configFile.Directory!,
                mappings,
                configureGlobalPackagesFolder: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (configureGlobalPackagesFolder)
            {
                await AddGlobalPackagesFolderToConfigAsync(configFile, globalPackagesFolderValue);
            }

            document = await LoadAsync(configFile, cancellationToken).ConfigureAwait(false);
            EnableMappedSources(document, mappings);
            await SaveAsync(document, configFile, cancellationToken).ConfigureAwait(false);
            return new TemporaryNuGetConfig(configFile, ContainsCredentials(document));
        }
        catch
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures; surface the original exception instead.
            }

            throw;
        }
    }

    /// <summary>
    /// Generates a NuGet.config file at the specified path with the given package mappings.
    /// </summary>
    public static async Task GenerateAsync(PackageMapping[] mappings, string targetPath)
    {
        var configFile = new FileInfo(targetPath);
        await GenerateNuGetConfigAsync(mappings, configFile);
    }

    private static async Task GenerateNuGetConfigAsync(PackageMapping[] mappings, FileInfo configFile)
    {
        var distinctSources = mappings
            .Select(m => m.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((source, index) => new { Source = source, Key = source })
            .ToArray();

        await using var fileStream = configFile.Create();
        await using var streamWriter = new StreamWriter(fileStream);
        await using var xmlWriter = XmlWriter.Create(streamWriter, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = Environment.NewLine,
            Encoding = System.Text.Encoding.UTF8,
            Async = true
        });

        await xmlWriter.WriteStartDocumentAsync();
        await xmlWriter.WriteStartElementAsync(null, "configuration", null);

        // Write packageSources section
        await xmlWriter.WriteStartElementAsync(null, "packageSources", null);

        // <clear />
        await xmlWriter.WriteStartElementAsync(null, "clear", null);
        await xmlWriter.WriteEndElementAsync();

        foreach (var sourceInfo in distinctSources)
        {
            await xmlWriter.WriteStartElementAsync(null, "add", null);
            await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceInfo.Key);
            await xmlWriter.WriteAttributeStringAsync(null, "value", null, sourceInfo.Source);
            await xmlWriter.WriteEndElementAsync(); // add
        }
        await xmlWriter.WriteEndElementAsync(); // packageSources

        // Add package source mappings for all filters
        if (mappings.Length > 0)
        {
            await xmlWriter.WriteStartElementAsync(null, "packageSourceMapping", null);

            var groupedBySource = mappings
                .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase);

            foreach (var sourceGroup in groupedBySource)
            {
                var sourceInfo = distinctSources.First(s => string.Equals(s.Source, sourceGroup.Key, StringComparison.OrdinalIgnoreCase));

                await xmlWriter.WriteStartElementAsync(null, "packageSource", null);
                await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceInfo.Key);

                foreach (var mapping in sourceGroup)
                {
                    await xmlWriter.WriteStartElementAsync(null, "package", null);
                    await xmlWriter.WriteAttributeStringAsync(null, "pattern", null, mapping.PackageFilter);
                    await xmlWriter.WriteEndElementAsync(); // package
                }

                await xmlWriter.WriteEndElementAsync(); // packageSource
            }

            await xmlWriter.WriteEndElementAsync(); // packageSourceMapping
        }

        await xmlWriter.WriteEndElementAsync(); // configuration
        await xmlWriter.WriteEndDocumentAsync();
    }

    private static async Task AddGlobalPackagesFolderToConfigAsync(FileInfo configFile, string? globalPackagesFolderValue)
    {
        var document = await LoadAsync(configFile, CancellationToken.None).ConfigureAwait(false);

        var configuration = document.Root ?? new XElement("configuration");
        if (document.Root is null)
        {
            document.Add(configuration);
        }

        NuGetConfigMerger.AddGlobalPackagesFolderConfiguration(configuration, globalPackagesFolderValue);

        var content = document.Declaration is null
            ? document.ToString()
            : $"{document.Declaration}{Environment.NewLine}{document}";
        await File.WriteAllTextAsync(configFile.FullName, content);
    }

    private static async Task<XDocument> LoadAsync(FileInfo configFile, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            configFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveAsync(XDocument document, FileInfo configFile, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            configFile.FullName,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await document.SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static bool ContainsCredentials(XDocument document)
    {
        var configuration = document.Root;
        if (configuration?.Elements().Any(static element =>
            (string.Equals(element.Name.LocalName, "packageSourceCredentials", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(element.Name.LocalName, "clientCertificates", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(element.Name.LocalName, "apikeys", StringComparison.OrdinalIgnoreCase)) &&
            element.Elements().Any()) == true)
        {
            return true;
        }

        if (configuration?.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "config", StringComparison.OrdinalIgnoreCase))
            ?.Elements()
            .Any(static element =>
                string.Equals(element.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase) &&
                element.Attributes().Any(attribute =>
                    string.Equals(attribute.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase) &&
                    attribute.Value.Contains("password", StringComparison.OrdinalIgnoreCase))) == true)
        {
            return true;
        }

        return configuration?.Elements()
            .Where(element =>
                string.Equals(element.Name.LocalName, "packageSources", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "auditSources", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static element => element.Elements())
            .Select(static element => element.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "value", StringComparison.OrdinalIgnoreCase))
                ?.Value)
            .Any(static source => source is not null && PackageSourceOverrideMappings.HasCredentialMaterial(source)) == true;
    }

    private static void EnableMappedSources(XDocument document, PackageMapping[] mappings)
    {
        var configuration = document.Root;
        var packageSources = configuration?.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "packageSources", StringComparison.OrdinalIgnoreCase));
        var mappedSourceKeys = packageSources?.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase))
            .Where(element => mappings.Any(mapping =>
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "value", StringComparison.OrdinalIgnoreCase))?.Value,
                    mapping.Source,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(element => element.Attributes().FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(static key => !string.IsNullOrEmpty(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mappedSourceKeys is not { Count: > 0 })
        {
            return;
        }

        var disabledSources = configuration?.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "disabledPackageSources", StringComparison.OrdinalIgnoreCase));
        disabledSources?.Elements()
            .Where(element => mappedSourceKeys.Contains(element.Attributes().FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase))?.Value))
            .Remove();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                if (_configFile.Exists)
                {
                    _configFile.Delete();
                    _configFile.Directory?.Delete(true);
                }
            }
            catch
            {
                // Ignore exceptions during cleanup
            }

            _disposed = true;
        }
    }
}
