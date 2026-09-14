// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using MongoDB.Driver.Core.Compression;
using Snappier;
using Xunit;

namespace Aspire.MongoDB.Driver.Tests;

public class SnappyCompressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1024, false)]
    [InlineData(131072, false)]
    [InlineData(131072, true)]
    public void DriverCompressorRoundTrips(int length, bool randomize)
    {
        var data = new byte[length];
        if (randomize)
        {
            new Random(42).NextBytes(data);
        }
        else
        {
            Array.Fill(data, (byte)'a');
        }

        var compressor = CreateDriverCompressor();
        using var input = new MemoryStream(data);
        using var compressed = new MemoryStream();
        compressor.Compress(input, compressed);

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        compressor.Decompress(compressed, decompressed);

        Assert.Equal(data, decompressed.ToArray());
    }

    [Fact]
    public void DriverCompressorReadsRawSnappyBlock()
    {
        // Raw Snappy: uncompressed length 5, literal tag (5 - 1) << 2, then "hello".
        // MongoDB uses raw blocks, not the framed SnappyStream format.
        // https://github.com/google/snappy/blob/main/format_description.txt
        byte[] data = [0x05, 0x10, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'];
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();

        CreateDriverCompressor().Decompress(input, output);

        Assert.Equal("hello"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public void FramedStreamRoundTrips()
    {
        var data = new byte[131072];
        new Random(42).NextBytes(data);
        using var compressed = new MemoryStream();
        using (var compressor = new SnappyStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(data);
        }

        compressed.Position = 0;
        using var decompressor = new SnappyStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public async Task MalformedFramedStreamThrows()
    {
        // The advisory's 15-byte frame has a compressed chunk containing only a checksum.
        // Keep the test's wait bounded if the vulnerable non-terminating decoder is restored.
        // https://github.com/advisories/GHSA-pggp-6c3x-2xmx
        await Task.Run(() =>
        {
            byte[] data = [0x00, 0x04, 0x00, 0x00, 0x64, 0x4e, 0x6c, 0x71, 0x79, 0x20, 0x77, 0x6f, 0x72, 0x6c, 0x64];
            using var input = new MemoryStream(data);
            using var decompressor = new SnappyStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            Assert.Throws<InvalidDataException>(() => decompressor.CopyTo(output));
        }).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static ICompressor CreateDriverCompressor()
    {
        // The v2 implementation is internal. Exercise the shipped driver binary, rather than
        // duplicating its Snappier calls, to catch binary incompatibility with the dependency.
        var type = typeof(ICompressor).Assembly.GetType("MongoDB.Driver.Core.Compression.SnappyCompressor", throwOnError: true)!;

        return Assert.IsAssignableFrom<ICompressor>(Activator.CreateInstance(type));
    }
}
