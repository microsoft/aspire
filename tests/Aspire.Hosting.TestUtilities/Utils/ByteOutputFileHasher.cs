// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Hashing;
using Aspire.Hosting.Pipelines;

namespace Aspire.Tests.Utils;

// The specification's streaming byte example, with stream observation for disposal assertions.
public sealed class ByteOutputFileHasher : IPipelineOutputFileHasher
{
    // Keep references after disposal so tests can verify that hashing closes its streams.
    public ConcurrentBag<Stream> Streams { get; } = [];

    public async ValueTask<ReadOnlyMemory<byte>> HashAsync(FileSystemInfo entry, CancellationToken ct)
    {
        using var stream = File.OpenRead(entry.FullName);
        Streams.Add(stream);
        var hash = new XxHash3();
        await hash.AppendAsync(stream, ct);
        return hash.GetCurrentHash();
    }
}
