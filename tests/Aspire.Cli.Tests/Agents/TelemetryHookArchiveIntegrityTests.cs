// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class TelemetryHookArchiveIntegrityTests
{
    [Fact]
    public async Task ReadEmbeddedAsync_ValidatesTheRetainedArchiveAndCompleteHookSet()
    {
        var archive = await TelemetryHookArchiveReader.ReadEmbeddedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(40, archive.CommitSha.Length);
        Assert.Equal(["track-telemetry.sh", "track-telemetry.ps1"], archive.Hooks.Select(hook => hook.Name));
        Assert.All(archive.Hooks, hook =>
        {
            Assert.NotEmpty(hook.Content);
            Assert.Equal(hook.ManifestSha512, hook.MetadataSha512);
            Assert.Equal(128, hook.ManifestSha512.Length);
        });
    }

    [Fact]
    public async Task ReadVerifiedAsync_VerifiesArchiveHashBeforeDecompression()
    {
        var data = new TelemetryHookArchiveTestData();
        data.Metadata["sha512"] = new string('0', 128);

        // These bytes are not gzip. A decompression error instead of this hash error
        // would prove entries were being read before the whole-archive trust check.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync([1, 2, 3])).DefaultTimeout();

        Assert.Equal("Archive SHA-512 does not match metadata.", error.Message);
    }

    [Fact]
    public async Task ReadVerifiedAsync_DoesNotSubstituteLegacyHashesForArchiveSha512()
    {
        var data = new TelemetryHookArchiveTestData();
        var archive = data.CreateArchive();
        data.Metadata.Remove("sha512");
        data.Metadata["sha256"] = new string('0', 64);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal("Expected exactly one JSON property named 'sha512'.", error.Message);
    }

    [Theory]
    [InlineData("skill-manifest.json")]
    [InlineData("hooks/scripts/track-telemetry.sh")]
    [InlineData("hooks/scripts/track-telemetry.ps1")]
    public async Task ReadVerifiedAsync_RejectsDuplicateExpectedEntries(string relativePath)
    {
        var data = new TelemetryHookArchiveTestData();
        var name = $"{TelemetryHookArchiveTestData.ArchiveRoot}/{relativePath}";
        data.Entries.Add(data.Entries.Single(entry => entry.Name == name));
        var archive = data.CreateArchive();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal($"Duplicate archive entry '{name}'.", error.Message);
    }

    [Theory]
    [InlineData("skill-manifest.json")]
    [InlineData("hooks/scripts/track-telemetry.sh")]
    [InlineData("hooks/scripts/track-telemetry.ps1")]
    public async Task ReadVerifiedAsync_RejectsMissingExpectedEntries(string relativePath)
    {
        var data = new TelemetryHookArchiveTestData();
        var name = $"{TelemetryHookArchiveTestData.ArchiveRoot}/{relativePath}";
        data.Entries.RemoveAll(entry => entry.Name == name);
        var archive = data.CreateArchive();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal($"Missing archive entry '{name}'.", error.Message);
    }

    [Theory]
    [InlineData("skill-manifest.json")]
    [InlineData("hooks/scripts/track-telemetry.sh")]
    [InlineData("hooks/scripts/track-telemetry.ps1")]
    public async Task ReadVerifiedAsync_RequiresCompleteEntryPaths(string relativePath)
    {
        var data = new TelemetryHookArchiveTestData();
        var name = $"{TelemetryHookArchiveTestData.ArchiveRoot}/{relativePath}";
        var index = data.Entries.FindIndex(entry => entry.Name == name);
        data.Entries[index] = data.Entries[index] with { Name = $"other-root/{relativePath}" };
        var archive = data.CreateArchive();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal($"Missing archive entry '{name}'.", error.Message);
    }

    [Theory]
    [InlineData("skill-manifest.json", TarEntryType.SymbolicLink)]
    [InlineData("skill-manifest.json", TarEntryType.HardLink)]
    [InlineData("skill-manifest.json", TarEntryType.Directory)]
    [InlineData("hooks/scripts/track-telemetry.sh", TarEntryType.SymbolicLink)]
    [InlineData("hooks/scripts/track-telemetry.sh", TarEntryType.HardLink)]
    [InlineData("hooks/scripts/track-telemetry.sh", TarEntryType.Directory)]
    [InlineData("hooks/scripts/track-telemetry.ps1", TarEntryType.SymbolicLink)]
    [InlineData("hooks/scripts/track-telemetry.ps1", TarEntryType.HardLink)]
    [InlineData("hooks/scripts/track-telemetry.ps1", TarEntryType.Directory)]
    public async Task ReadVerifiedAsync_RejectsNonregularExpectedEntries(string relativePath, TarEntryType type)
    {
        var data = new TelemetryHookArchiveTestData();
        var index = data.Entries.FindIndex(entry => entry.Name == $"{TelemetryHookArchiveTestData.ArchiveRoot}/{relativePath}");
        data.Entries[index] = data.Entries[index] with { Type = type };
        var archive = data.CreateArchive();

        await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();
    }

    [Fact]
    public async Task ReadVerifiedAsync_RequiresMatchingHookCommitIdentities()
    {
        var data = new TelemetryHookArchiveTestData();
        data.Manifest["hooks"]!["commitSha"] = new string('a', 40);
        var archive = data.CreateArchive();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal("Manifest and metadata hook commit identities differ.", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-commit")]
    public async Task ReadVerifiedAsync_RejectsMissingOrInvalidCommitIdentitiesEvenWhenTheyAgree(string? commit)
    {
        var data = new TelemetryHookArchiveTestData();
        data.Manifest["hooks"]!["commitSha"] = commit;
        data.Metadata["hooks"]!["commitSha"] = commit;
        var archive = data.CreateArchive();

        await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadVerifiedAsync_RequiresBothHookMetadataBlocks(bool manifest)
    {
        var data = new TelemetryHookArchiveTestData();
        (manifest ? data.Manifest : data.Metadata).Remove("hooks");
        var archive = data.CreateArchive();

        await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ReadVerifiedAsync_RequiresCompleteManifestAndMetadataHookNames(bool manifest, bool extra)
    {
        var data = new TelemetryHookArchiveTestData();
        var files = (manifest ? data.Manifest : data.Metadata)["hooks"]!["files"]!.AsObject();
        if (extra)
        {
            files["unexpected.sh"] = new string('0', 128);
        }
        else
        {
            files.Remove("track-telemetry.sh");
        }

        var archive = data.CreateArchive();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal("Hook metadata must contain exactly the two expected script names.", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadVerifiedAsync_RejectsDuplicateHookHashDeclarations(bool manifest)
    {
        var data = new TelemetryHookArchiveTestData();
        if (manifest)
        {
            data.Entries[0] = data.Entries[0] with { Content = Encoding.UTF8.GetBytes(DuplicateHookHash(data.Manifest)) };
        }

        var archiveBytes = data.CreateArchive();
        var metadataJson = manifest ? data.Metadata.ToJsonString() : DuplicateHookHash(data.Metadata);
        using var archive = new MemoryStream(archiveBytes);
        using var metadata = new MemoryStream(Encoding.UTF8.GetBytes(metadataJson));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TelemetryHookArchiveReader.ReadVerifiedAsync(archive, metadata, TestContext.Current.CancellationToken)).DefaultTimeout();
    }

    [Theory]
    [InlineData("track-telemetry.sh", true)]
    [InlineData("track-telemetry.ps1", true)]
    [InlineData("track-telemetry.sh", false)]
    [InlineData("track-telemetry.ps1", false)]
    public async Task ReadVerifiedAsync_RequiresBothPerHookSha512Hashes(string name, bool manifest)
    {
        var data = new TelemetryHookArchiveTestData();
        (manifest ? data.Manifest : data.Metadata)["hooks"]!["files"]![name] = new string('0', 128);
        var archive = data.CreateArchive();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();

        Assert.Equal($"{(manifest ? "Manifest" : "Metadata")} SHA-512 does not match hook '{name}'.", error.Message);
    }

    [Theory]
    [InlineData("track-telemetry.sh")]
    [InlineData("track-telemetry.ps1")]
    public async Task ReadVerifiedAsync_RejectsChangedHookBytesEvenWithAnUpdatedArchiveHash(string name)
    {
        var data = new TelemetryHookArchiveTestData();
        var index = data.Entries.FindIndex(entry => entry.Name == $"{TelemetryHookArchiveTestData.ArchiveRoot}/hooks/scripts/{name}");
        data.Entries[index] = data.Entries[index] with { Content = "changed hook"u8.ToArray() };
        var archive = data.CreateArchive();

        await Assert.ThrowsAsync<InvalidDataException>(() => data.ReadAsync(archive)).DefaultTimeout();
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    [InlineData("\r\n", true)]
    public async Task ReadVerifiedAsync_UsesLfUtf8HashesForHookBytes(string newline, bool bom)
    {
        var data = new TelemetryHookArchiveTestData();
        var canonical = data.Entries.Skip(1).ToArray();
        for (var i = 1; i < data.Entries.Count; i++)
        {
            var text = Encoding.UTF8.GetString(data.Entries[i].Content!).Replace("\n", newline, StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(text);
            data.Entries[i] = data.Entries[i] with { Content = bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes };
        }

        var archive = await data.ReadAsync(data.CreateArchive()).DefaultTimeout();

        Assert.Equal(canonical.Select(entry => entry.Name.Split('/')[^1]), archive.Hooks.Select(hook => hook.Name));
        for (var i = 0; i < canonical.Length; i++)
        {
            Assert.Equal(canonical[i].Content, archive.Hooks[i].Content);
        }
    }

    private static string DuplicateHookHash(JsonObject document)
    {
        // JsonObject rejects duplicates itself. Produce raw JSON such as
        // "files":{"track-telemetry.sh":"...","track-telemetry.sh":"...",...}
        // to prove the reader does not silently accept the last declaration.
        var hash = document["hooks"]!["files"]!["track-telemetry.sh"]!.GetValue<string>();
        return document.ToJsonString().Replace("\"files\":{", $"\"files\":{{\"track-telemetry.sh\":\"{hash}\",", StringComparison.Ordinal);
    }
}
