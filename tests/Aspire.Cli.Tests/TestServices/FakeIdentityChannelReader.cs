// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// In-memory <see cref="IIdentityChannelReader"/> for tests. Returns a fixed
/// channel string, or returns false when configured to simulate a misconfigured
/// build with missing <c>AspireCliChannel</c> assembly metadata.
/// </summary>
internal sealed class FakeIdentityChannelReader : IIdentityChannelReader
{
    private readonly string? _channel;
    private readonly bool _fail;

    public FakeIdentityChannelReader(string channel)
    {
        _channel = channel;
        _fail = false;
    }

    public FakeIdentityChannelReader(bool failOnRead)
    {
        _channel = null;
        _fail = failOnRead;
    }

    public bool TryReadChannel([NotNullWhen(true)] out string? channel, [NotNullWhen(false)] out string? error)
    {
        if (_fail)
        {
            channel = null;
            error = "Simulated missing AspireCliChannel metadata.";
            return false;
        }

        channel = _channel!;
        error = null;
        return true;
    }
}
