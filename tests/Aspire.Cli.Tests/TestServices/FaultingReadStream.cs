// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FaultingReadStream(byte[] data, Exception exception) : MemoryStream(data)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await base.ReadAsync(buffer, cancellationToken);
        if (count == 0)
        {
            throw exception;
        }

        return count;
    }
}
