// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.TestServices;

internal static class TelemetryHookArchiveReader
{
    private static readonly string[] s_hookNames = ["track-telemetry.sh", "track-telemetry.ps1"];

    public static async Task<VerifiedTelemetryHookArchive> ReadEmbeddedAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(TelemetryHookInstaller).Assembly;
        await using var archive = assembly.GetManifestResourceStream("aspire-skills.bundle.tgz")
            ?? throw new InvalidDataException("The retained hook verification archive is missing.");
        await using var metadata = assembly.GetManifestResourceStream("aspire-skills.metadata.json")
            ?? throw new InvalidDataException("The retained hook verification metadata is missing.");

        return await ReadVerifiedAsync(archive, metadata, cancellationToken);
    }

    public static async Task<VerifiedTelemetryHookArchive> ReadVerifiedAsync(
        Stream archiveStream, Stream metadataStream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);
        using var archive = new MemoryStream();
        await archiveStream.CopyToAsync(archive, cancellationToken);

        // Authenticate the complete compressed archive before creating a decompressor or
        // reading entries. Per-hook hashes must not replace this metadata trust boundary.
        VerifySha512(archive.GetBuffer().AsSpan(0, checked((int)archive.Length)),
            RequiredString(metadata.RootElement, "sha512"), "Archive SHA-512 does not match metadata.");
        archive.Position = 0;

        var assetName = RequiredString(metadata.RootElement, "assetName");
        var archiveRoot = assetName.EndsWith(".tar.gz", StringComparison.Ordinal) ? assetName[..^7]
            : assetName.EndsWith(".tgz", StringComparison.Ordinal) ? assetName[..^4]
            : throw new InvalidDataException("Expected a gzip-compressed tar verification archive.");
        if (!archiveRoot.StartsWith("aspire-skills-", StringComparison.Ordinal) ||
            archiveRoot.Contains('/') || archiveRoot.Contains('\\'))
        {
            throw new InvalidDataException("The verification archive must have an Aspire skills asset name.");
        }

        // Tar member names are POSIX paths, not filesystem paths. For example:
        // aspire-skills-v0.0.2/skill-manifest.json and
        // aspire-skills-v0.0.2/hooks/scripts/track-telemetry.sh.
        // Require complete names; basename matching could accept a different tree.
        var manifestPath = $"{archiveRoot}/skill-manifest.json";
        var expectedPaths = s_hookNames.Select(name => $"{archiveRoot}/hooks/scripts/{name}").Prepend(manifestPath).ToArray();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: cancellationToken) is { } entry)
        {
            if (!expectedPaths.Contains(entry.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (files.ContainsKey(entry.Name))
            {
                throw new InvalidDataException($"Duplicate archive entry '{entry.Name}'.");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                throw new InvalidDataException($"Expected a regular archive file for '{entry.Name}'.");
            }

            using var content = new MemoryStream();
            await entry.DataStream.CopyToAsync(content, cancellationToken);
            files.Add(entry.Name, content.ToArray());
        }

        foreach (var path in expectedPaths)
        {
            if (!files.ContainsKey(path))
            {
                throw new InvalidDataException($"Missing archive entry '{path}'.");
            }
        }

        using var manifest = JsonDocument.Parse(files[manifestPath]);
        var manifestHooks = RequiredProperty(manifest.RootElement, "hooks");
        var metadataHooks = RequiredProperty(metadata.RootElement, "hooks");
        var manifestCommit = ReadCommit(manifestHooks);
        var metadataCommit = ReadCommit(metadataHooks);
        if (!string.Equals(manifestCommit, metadataCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest and metadata hook commit identities differ.");
        }

        var manifestHashes = ReadHookHashes(manifestHooks);
        var metadataHashes = ReadHookHashes(metadataHooks);
        var hooks = new List<VerifiedTelemetryHook>();
        foreach (var name in s_hookNames)
        {
            var content = NormalizeHookBytes(files[$"{archiveRoot}/hooks/scripts/{name}"]);
            VerifySha512(content, manifestHashes[name], $"Manifest SHA-512 does not match hook '{name}'.");
            VerifySha512(content, metadataHashes[name], $"Metadata SHA-512 does not match hook '{name}'.");
            hooks.Add(new(name, content, manifestHashes[name], metadataHashes[name]));
        }

        return new(manifestCommit, hooks.AsReadOnly());
    }

    public static byte[] NormalizeHookBytes(ReadOnlySpan<byte> content)
    {
        // Match the retained verifier's LF UTF-8 contract: remove a leading UTF-8 BOM
        // (EF BB BF), then normalize CRLF and standalone CR without changing other text.
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (content.StartsWith(bom))
        {
            content = content[3..];
        }

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        return Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    }

    private static void VerifySha512(ReadOnlySpan<byte> content, string expected, string error)
    {
        if (!string.Equals(Convert.ToHexStringLower(SHA512.HashData(content)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(error);
        }
    }

    private static string ReadCommit(JsonElement hooks)
    {
        var commit = RequiredString(hooks, "commitSha");
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Hook metadata must identify a full Git commit SHA.");
        }

        return commit;
    }

    private static Dictionary<string, string> ReadHookHashes(JsonElement hooks)
    {
        var files = RequiredProperty(hooks, "files");
        if (files.ValueKind is not JsonValueKind.Object ||
            !files.EnumerateObject().Select(file => file.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(s_hookNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Hook metadata must contain exactly the two expected script names.");
        }

        return s_hookNames.ToDictionary(name => name, name => RequiredString(files, name).ToLowerInvariant(), StringComparer.Ordinal);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var property = RequiredProperty(parent, name);
        return property.ValueKind is JsonValueKind.String && property.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Expected a nonempty string for '{name}'.");
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException($"Expected a JSON object containing '{name}'.");
        }

        var properties = parent.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return properties.Length == 1
            ? properties[0].Value
            : throw new InvalidDataException($"Expected exactly one JSON property named '{name}'.");
    }
}

internal sealed record VerifiedTelemetryHookArchive(string CommitSha, IReadOnlyList<VerifiedTelemetryHook> Hooks);

internal sealed record VerifiedTelemetryHook(string Name, byte[] Content, string ManifestSha512, string MetadataSha512);
