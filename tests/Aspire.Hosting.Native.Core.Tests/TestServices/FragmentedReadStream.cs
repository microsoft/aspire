// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class FragmentedReadStream(byte[] content, int maximumReadBytes) : MemoryStream(content)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadBytes)], cancellationToken);
}
