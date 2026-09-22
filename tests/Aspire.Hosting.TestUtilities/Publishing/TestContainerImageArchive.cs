// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.Tests.Publishing;

public static class TestContainerImageArchive
{
    private const string FixtureConfig = """
        {
          "architecture": "amd64",
          "config": {
            "Env": [ "PATH=/usr/local/bin" ],
            "Labels": {
              "aspire.test.fixture": "true"
            }
          },
          "created": "1970-01-01T00:00:00Z",
          "os": "linux",
          "rootfs": {
            "type": "layers",
            "diff_ids": [
              "sha256:0000000000000000000000000000000000000000000000000000000000000000"
            ]
          }
        }
        """;

    public static void WriteDockerArchive(string archivePath, string imageReference, string layerContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(layerContents);

        var (repository, tag) = SplitTaggedReference(imageReference);
        var manifest = JsonSerializer.Serialize(
        new[]
        {
            new
            {
                Config = "config.json",
                RepoTags = new[] { imageReference },
                Layers = new[] { "layer.tar" }
            }
        });
        var repositories = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [repository] = new()
            {
                [tag] = "layer"
            }
        });

        WriteArchive(
            archivePath,
            layerContents,
            ("manifest.json", manifest),
            ("repositories", repositories),
            ("config.json", FixtureConfig));
    }

    public static string[] ReadDockerImageReferences(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = new TarReader(archiveStream);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (GetRootEntryName(entry.Name) != "manifest.json")
            {
                continue;
            }

            using var document = JsonDocument.Parse(
                entry.DataStream ?? throw new InvalidDataException("The Docker manifest entry has no data."));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The Docker manifest root must be an array.");
            }

            var references = new List<string>();
            foreach (var manifest in document.RootElement.EnumerateArray())
            {
                if (!manifest.TryGetProperty("RepoTags", out var repoTags) ||
                    repoTags.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (repoTags.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Docker manifest RepoTags must be an array.");
                }

                foreach (var reference in repoTags.EnumerateArray())
                {
                    references.Add(reference.GetString()
                        ?? throw new InvalidDataException("Docker manifest RepoTags must contain strings."));
                }
            }

            return references.ToArray();
        }

        throw new InvalidDataException("The archive does not contain manifest.json.");
    }

    public static string ReadLayerContents(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = new TarReader(archiveStream);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (!string.Equals(GetRootEntryName(entry.Name), "layer.tar", StringComparison.Ordinal))
            {
                continue;
            }

            using var layerArchive = new MemoryStream();
            (entry.DataStream ?? throw new InvalidDataException("The layer.tar entry has no data.")).CopyTo(layerArchive);
            layerArchive.Position = 0;

            using var layerReader = new TarReader(layerArchive);
            while (layerReader.GetNextEntry(copyData: false) is { } layerEntry)
            {
                if (layerEntry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                using var textReader = new StreamReader(
                    layerEntry.DataStream ?? throw new InvalidDataException("The layer payload entry has no data."),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                return textReader.ReadToEnd();
            }

            throw new InvalidDataException("The fixture layer.tar does not contain a regular file.");
        }

        throw new InvalidDataException("The archive does not contain layer.tar.");
    }

    internal static void WriteArchive(
        string archivePath,
        string layerContents,
        params (string Name, string Contents)[] textEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(layerContents);
        ArgumentNullException.ThrowIfNull(textEntries);

        using var archiveStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new TarWriter(archiveStream, leaveOpen: true);

        foreach (var (name, contents) in textEntries)
        {
            WriteTextEntry(writer, name, contents);
        }

        using var layerArchive = new MemoryStream();
        using (var layerWriter = new TarWriter(layerArchive, leaveOpen: true))
        {
            WriteTextEntry(layerWriter, "payload.txt", layerContents);
        }

        layerArchive.Position = 0;
        var layerEntry = new PaxTarEntry(TarEntryType.RegularFile, "layer.tar")
        {
            DataStream = layerArchive
        };
        writer.WriteEntry(layerEntry);
    }

    internal static string ReadEntryText(string archivePath, string entryName)
    {
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = new TarReader(archiveStream);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (!string.Equals(entry.Name, entryName, StringComparison.Ordinal))
            {
                continue;
            }

            using var textReader = new StreamReader(
                entry.DataStream ?? throw new InvalidDataException($"The '{entryName}' entry has no data."),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            return textReader.ReadToEnd();
        }

        throw new InvalidDataException($"The archive does not contain '{entryName}'.");
    }

    internal static string[] ReadEntryNames(string archivePath)
    {
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = new TarReader(archiveStream);
        var names = new List<string>();
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            names.Add(entry.Name);
        }

        return names.ToArray();
    }

    private static void WriteTextEntry(TarWriter writer, string name, string contents)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        using var data = new MemoryStream(bytes, writable: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data
        };
        writer.WriteEntry(entry);
    }

    private static Stream OpenArchiveStream(string archivePath)
    {
        var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[2];
        var bytesRead = file.Read(magic);
        file.Position = 0;

        // Detect compression from the gzip magic bytes rather than the extension because publishing
        // also supports custom output extensions and .tgz aliases.
        return bytesRead == magic.Length && magic[0] == 0x1f && magic[1] == 0x8b
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
            : file;
    }

    private static string? GetRootEntryName(string entryName)
    {
        while (entryName.StartsWith("./", StringComparison.Ordinal))
        {
            entryName = entryName[2..];
        }

        return entryName.Contains('/') ? null : entryName;
    }

    private static (string Repository, string Tag) SplitTaggedReference(string reference)
    {
        var lastSlash = reference.LastIndexOf('/');
        var lastColon = reference.LastIndexOf(':');
        if (lastColon <= lastSlash || lastColon <= 0 || lastColon == reference.Length - 1)
        {
            throw new ArgumentException($"'{reference}' is not a tagged image reference.", nameof(reference));
        }

        return (reference[..lastColon], reference[(lastColon + 1)..]);
    }
}
