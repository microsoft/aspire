// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AnalyzeCiFailure;

/// <summary>Validates and extracts bounded structured-result ZIP archives without artifact-selection policy.</summary>
internal static class TestResultArchiveExtractor
{
    internal static long ExtractTracked(
        string archivePath,
        string outputDirectory,
        long maxExpandedBytes,
        long? expectedBytes,
        string format,
        Action<int> expandedBytes)
        => ExtractCore(
            archivePath,
            outputDirectory,
            maxEntries: 10000,
            maxExpandedBytes,
            maxArchiveBytes: 104857600,
            expectedBytes,
            format,
            expandedBytes);

    internal static long Extract(
        string archivePath,
        string outputDirectory,
        int maxEntries = 10000,
        long maxExpandedBytes = 1073741824,
        long maxArchiveBytes = 104857600,
        long? expectedBytes = null,
        string format = "trx")
        => ExtractCore(archivePath, outputDirectory, maxEntries, maxExpandedBytes, maxArchiveBytes, expectedBytes, format, expandedBytes: null);

    private static long ExtractCore(
        string archivePath,
        string outputDirectory,
        int maxEntries,
        long maxExpandedBytes,
        long maxArchiveBytes,
        long? expectedBytes,
        string format,
        Action<int>? expandedBytes)
    {
        if (!(maxEntries > 0 && maxExpandedBytes > 0 && maxArchiveBytes > 0 && expectedBytes is not < 0 && format is "trx" or "mocha"))
        {
            throw new ValidationException("Invalid test results extraction budget");
        }
        using var file = File.OpenRead(archivePath);
        if (!(file.Length <= maxArchiveBytes))
        {
            throw new ValidationException($"downloaded archive exceeds the {maxArchiveBytes}-byte budget");
        }
        if (!(expectedBytes is null || file.Length == expectedBytes))
        {
            throw new ValidationException("downloaded archive size does not match artifact metadata");
        }
        var entryCount = ValidateCentralDirectory(file, maxEntries);
        if (!(!Directory.Exists(outputDirectory) && !File.Exists(outputDirectory)))
        {
            throw new ValidationException("test results output directory already exists");
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(outputDirectory) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (!(archive.Entries.Count == entryCount))
            {
                throw new ValidationException("archive entry count does not match its central directory");
            }
            long written = 0;
            var index = 0;
            var buffer = new byte[1024 * 1024];
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                var parts = name.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (!(parts.Length > 0 && !name.StartsWith('/') && !name.Contains('\\') && parts.All(p => p != "..")))
                {
                    throw new ValidationException("archive contains an unsafe path");
                }
                // Harmless "." and repeated separators are allowed, but ".." is not.
                // Extraction uses generated file names, never archived paths.
                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                if (name.EndsWith('/'))
                {
                    if (!(type is 0 or 0x4000))
                    {
                        throw new ValidationException("archive contains an unsupported file type");
                    }
                    continue;
                }
                if (!(type is 0 or 0x8000))
                {
                    throw new ValidationException("archive contains an unsupported file type");
                }
                // Encryption is rejected for every central-directory entry by CheckDirectory.
                if (!(format == "trx" ? name.EndsWith(".trx", StringComparison.Ordinal) : parts[^1] == "mocha.json"))
                {
                    continue;
                }
                var destination = Path.Combine(staging, $"{++index:D5}.{(format == "trx" ? "trx" : "json")}");
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew);
                long entryBytes = 0;
                uint crc = uint.MaxValue;
                int read;
                while ((read = input.Read(buffer, offset: 0, (int)Math.Min(buffer.Length, maxExpandedBytes - written + 1))) > 0)
                {
                    written += read;
                    expandedBytes?.Invoke(read);
                    entryBytes += read;
                    if (!(written <= maxExpandedBytes))
                    {
                        throw new ValidationException($"uncompressed data exceeds the {maxExpandedBytes}-byte budget");
                    }
                    foreach (var b in buffer.AsSpan(start: 0, read))
                    {
                        crc = (crc >> 8) ^ s_crcTable[(crc ^ b) & 255];
                    }
                    output.Write(buffer, offset: 0, read);
                }
                if (!(entryBytes == entry.Length && ~crc == entry.Crc32))
                {
                    throw new ValidationException("archive result data does not match its central directory");
                }
            }
            Directory.Move(staging, outputDirectory);

            return written;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static int ValidateCentralDirectory(Stream file, int maxEntries)
    {
        // Classic ZIP EOCD: signature + four UInt16 fields, directory size/offset
        // UInt32, UInt16 comment length. Bound actual central records before
        // ZipArchive materializes an attacker-controlled number of entry objects.
        // Record layouts: https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT (4.3.12, 4.3.16).
        var tail = new byte[(int)Math.Min(file.Length, 22 + 65535)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var position = tail.Length - 22;
        for (; position >= 0; position--)
        {
            if (tail.AsSpan(position, length: 4).SequenceEqual("PK\u0005\u0006"u8) &&
                position + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(position + 20)) == tail.Length)
            {
                break;
            }
        }
        if (!(position >= 0))
        {
            throw new ValidationException("archive has no valid end-of-central-directory record");
        }
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(position + offset));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(position + offset));
        if (!(U16(offset: 4) == 0 && U16(offset: 6) == 0 && U16(offset: 8) == U16(offset: 10)))
        {
            throw new ValidationException("multi-disk archives are unsupported");
        }
        if (!(!(position >= 20 && tail.AsSpan(position - 20, length: 4).SequenceEqual("PK\u0006\u0007"u8)) &&
            U16(offset: 10) != ushort.MaxValue && U32(offset: 12) != uint.MaxValue && U32(offset: 16) != uint.MaxValue))
        {
            throw new ValidationException("ZIP64 archives exceed the supported extraction limits");
        }
        var directorySize = (long)U32(offset: 12);
        var directoryOffset = (long)U32(offset: 16);
        if (!(directoryOffset + directorySize == file.Length - tail.Length + position))
        {
            throw new ValidationException("archive central directory bounds are invalid");
        }
        file.Position = directoryOffset;
        var header = new byte[46];
        var count = 0;
        long consumed = 0;
        while (consumed < directorySize)
        {
            if (!(file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length))
            {
                throw new ValidationException("archive central directory is truncated");
            }
            if (!header.AsSpan(start: 0, length: 4).SequenceEqual("PK\u0001\u0002"u8))
            {
                throw new ValidationException("archive central directory contains an invalid record");
            }
            var variable = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start: 28)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start: 30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start: 32));
            consumed += header.Length + variable;
            if (!(consumed <= directorySize))
            {
                throw new ValidationException("archive central directory record exceeds its bounds");
            }
            if (!(++count <= maxEntries))
            {
                throw new ValidationException($"archive contains more than the {maxEntries}-entry budget");
            }
            if (!((BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start: 8)) & 1) == 0))
            {
                throw new ValidationException("archive contains an encrypted entry");
            }
            file.Seek(variable, SeekOrigin.Current);
        }
        if (!(count == U16(offset: 10)))
        {
            throw new ValidationException("archive entry count does not match its central directory");
        }
        file.Position = 0;

        return count;
    }

    private static readonly uint[] s_crcTable = Enumerable.Range(start: 0, count: 256).Select(index =>
    {
        var crc = (uint)index;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        return crc;
    }).ToArray();
}
