// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;

namespace Aspire.Tests.Utils;

public sealed class TestPipelineOutputFileHasher(Func<FileSystemInfo, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> hash) : IPipelineOutputFileHasher
{
    public ValueTask<ReadOnlyMemory<byte>> HashAsync(FileSystemInfo entry, CancellationToken cancellationToken) => hash(entry, cancellationToken);
}
