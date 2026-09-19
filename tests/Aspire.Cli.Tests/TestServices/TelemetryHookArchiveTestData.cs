// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TelemetryHookArchiveTestData
{
    public const string ArchiveRoot = "aspire-skills-v0.0.2";
    public const string CommitSha = "842c4fb8d9ff5f89bf3e5f6f16739a728b5ba77e";

    public JsonObject Manifest { get; }
    public JsonObject Metadata { get; }
    public List<TelemetryHookArchiveEntry> Entries { get; }

    public TelemetryHookArchiveTestData()
    {
        var shell = "#!/usr/bin/env bash\necho 'aspire'\n"u8.ToArray();
        var powerShell = "Write-Output 'aspire'\n"u8.ToArray();
        var hashes = new JsonObject
        {
            ["track-telemetry.sh"] = Convert.ToHexStringLower(SHA512.HashData(shell)),
            ["track-telemetry.ps1"] = Convert.ToHexStringLower(SHA512.HashData(powerShell))
        };
        Manifest = new JsonObject
        {
            ["hooks"] = new JsonObject { ["commitSha"] = CommitSha, ["files"] = hashes }
        };
        Metadata = new JsonObject
        {
            ["assetName"] = $"{ArchiveRoot}.tgz",
            ["hooks"] = Manifest["hooks"]!.DeepClone()
        };
        Entries =
        [
            new($"{ArchiveRoot}/skill-manifest.json", TarEntryType.RegularFile, Content: null),
            new($"{ArchiveRoot}/hooks/scripts/track-telemetry.sh", TarEntryType.RegularFile, shell),
            new($"{ArchiveRoot}/hooks/scripts/track-telemetry.ps1", TarEntryType.RegularFile, powerShell)
        ];
    }

    public byte[] CreateArchive()
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var item in Entries)
            {
                // A null payload marks the manifest so mutations to its JSON, including
                // duplicate or renamed manifest entries, are reflected in the tar bytes.
                using var content = new MemoryStream(item.Content ?? Encoding.UTF8.GetBytes(Manifest.ToJsonString()));
                var entry = new PaxTarEntry(item.Type, item.Name)
                {
                    ModificationTime = DateTimeOffset.UnixEpoch
                };
                if (item.Type is TarEntryType.RegularFile)
                {
                    entry.DataStream = content;
                }
                else if (item.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    entry.LinkName = "other-script";
                }

                writer.WriteEntry(entry);
            }
        }

        var bytes = archive.ToArray();
        // Entry and manifest mutations must pass the whole-archive gate so negative tests
        // actually exercise the independent entry, identity, and per-hook checks.
        Metadata["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes));
        return bytes;
    }

    public async Task<VerifiedTelemetryHookArchive> ReadAsync(byte[] archiveBytes)
    {
        using var archive = new MemoryStream(archiveBytes);
        using var metadata = new MemoryStream(Encoding.UTF8.GetBytes(Metadata.ToJsonString()));
        return await TelemetryHookArchiveReader.ReadVerifiedAsync(archive, metadata, TestContext.Current.CancellationToken);
    }
}

internal sealed record TelemetryHookArchiveEntry(string Name, TarEntryType Type, byte[]? Content);
