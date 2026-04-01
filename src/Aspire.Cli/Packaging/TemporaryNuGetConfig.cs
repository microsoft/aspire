// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;

namespace Aspire.Cli.Packaging;

internal sealed class TemporaryNuGetConfig : IDisposable
{
    private readonly FileInfo _configFile;
    private bool _disposed;

    private TemporaryNuGetConfig(FileInfo configFile)
    {
        _configFile = configFile;
    }

    public FileInfo ConfigFile => _configFile;

    public static async Task<TemporaryNuGetConfig> CreateAsync(PackageMapping[] mappings)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        var tempFilePath = Path.Combine(tempDirectory, "nuget.config");
        var configFile = new FileInfo(tempFilePath);
        await GenerateNuGetConfigAsync(mappings, configFile);
        return new TemporaryNuGetConfig(configFile);
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
        // Build distinct sources with their preferred key names.
        // If any mapping for a source specifies a Key, use that; otherwise fall back to the source URL.
        var sourceKeyLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings)
        {
            if (!sourceKeyLookup.ContainsKey(m.Source))
            {
                sourceKeyLookup[m.Source] = m.Key ?? m.Source;
            }
            else if (m.Key is not null)
            {
                sourceKeyLookup[m.Source] = m.Key;
            }
        }

        var distinctSources = sourceKeyLookup
            .Select(kvp => new { Source = kvp.Key, Key = kvp.Value })
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
