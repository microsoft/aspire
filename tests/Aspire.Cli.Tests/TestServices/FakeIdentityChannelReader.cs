// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// In-memory <see cref="IIdentityChannelReader"/> for tests. Returns a fixed
/// channel string, or throws <see cref="InvalidOperationException"/> when
/// configured to simulate a misconfigured build with missing
/// <c>AspireCliChannel</c> assembly metadata.
/// </summary>
internal sealed class FakeIdentityChannelReader : IIdentityChannelReader
{
    private readonly string? _channel;
    private readonly bool _throw;

    public FakeIdentityChannelReader(string channel)
    {
        _channel = channel;
        _throw = false;
    }

    public FakeIdentityChannelReader(bool throwOnRead)
    {
        _channel = null;
        _throw = throwOnRead;
    }

    public string ReadChannel()
    {
        if (_throw)
        {
            throw new InvalidOperationException("Simulated missing AspireCliChannel metadata.");
        }

        return _channel!;
    }
}
