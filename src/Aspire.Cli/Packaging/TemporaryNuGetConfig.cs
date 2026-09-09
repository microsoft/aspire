// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

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
            await GenerateNuGetConfigAsync(mappings, configFile).ConfigureAwait(false);
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
        Func<string, Task> writeConfigAsync)
    {
        ArgumentNullException.ThrowIfNull(writeConfigAsync);

        var tempDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config").FullName;
        try
        {
            var configFile = new FileInfo(Path.Combine(tempDirectory, "nuget.config"));
            await writeConfigAsync(configFile.FullName).ConfigureAwait(false);

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
        => GenerateNuGetConfigAsync(mappings, new FileInfo(targetPath));

    public async Task RegenerateAsync(Func<string, Task> writeConfigAsync)
    {
        ArgumentNullException.ThrowIfNull(writeConfigAsync);

        await writeConfigAsync(_configFile.FullName).ConfigureAwait(false);
        CacheIdentity = await ComputeCacheIdentityAsync(_configFile).ConfigureAwait(false);
    }

    private static async Task GenerateNuGetConfigAsync(
        PackageMapping[] mappings,
        FileInfo configFile)
    {
        var sources = mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer)
            .Select(static (source, index) => (Key: $"aspire-{index}", Source: source))
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

        await xmlWriter.WriteStartElementAsync(null, "packageSources", null);
        await xmlWriter.WriteStartElementAsync(null, "clear", null);
        await xmlWriter.WriteEndElementAsync();

        foreach (var source in sources)
        {
            await xmlWriter.WriteStartElementAsync(null, "add", null);
            await xmlWriter.WriteAttributeStringAsync(null, "key", null, source.Key);
            await xmlWriter.WriteAttributeStringAsync(null, "value", null, source.Source);
            await xmlWriter.WriteEndElementAsync();
        }

        await xmlWriter.WriteEndElementAsync();

        if (mappings.Length > 0)
        {
            await xmlWriter.WriteStartElementAsync(null, "packageSourceMapping", null);

            foreach (var sourceGroup in mappings.GroupBy(static mapping => mapping.Source, PackageSourceIdentity.Comparer))
            {
                var sourceKey = sources
                    .Single(source => PackageSourceIdentity.Comparer.Equals(source.Source, sourceGroup.Key))
                    .Key;
                await xmlWriter.WriteStartElementAsync(null, "packageSource", null);
                await xmlWriter.WriteAttributeStringAsync(null, "key", null, sourceKey);

                foreach (var mapping in sourceGroup)
                {
                    await xmlWriter.WriteStartElementAsync(null, "package", null);
                    await xmlWriter.WriteAttributeStringAsync(null, "pattern", null, mapping.PackageFilter);
                    await xmlWriter.WriteEndElementAsync();
                }

                await xmlWriter.WriteEndElementAsync();
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

    private static async Task<string> ComputeCacheIdentityAsync(FileInfo configFile)
    {
        var document = await LoadAsync(configFile).ConfigureAwait(false);
        var globalPackagesFolderItems = document
            .Descendants("config")
            .Elements("add")
            .Where(static element => string.Equals(
                (string?)element.Attribute("key"),
                "globalPackagesFolder",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        globalPackagesFolderItems.Remove();
        document
            .Descendants("config")
            .Where(static section => !section.Elements().Any())
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
