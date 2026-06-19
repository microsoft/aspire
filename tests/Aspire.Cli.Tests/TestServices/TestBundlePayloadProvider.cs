// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test payload provider that returns a fresh <see cref="MemoryStream"/> from
/// a pre-built <c>byte[]</c> on each call to <see cref="OpenPayload"/>.
/// </summary>
internal sealed class TestBundlePayloadProvider : IBundlePayloadProvider
{
    private readonly byte[] _payload;

    public TestBundlePayloadProvider(byte[] payload)
    {
        _payload = payload;
    }

    public bool HasPayload => _payload.Length > 0;

    public Stream? OpenPayload() => HasPayload ? new MemoryStream(_payload) : null;
}
