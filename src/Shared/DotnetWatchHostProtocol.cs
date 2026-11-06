// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Shared;

internal sealed class DotnetWatchHostInvalidation
{
    public int Version { get; init; } = 1;
    public required string Session { get; init; }
    public int Kind { get; init; }
    public string? Instance { get; init; }
}

internal static class DotnetWatchHostProtocol
{
    internal const int MaximumMessageBytes = 4096;
    internal const int ModelInvalidated = 0;
    internal const int HostStarted = 1;

    internal static async Task WriteAsync(Stream stream, DotnetWatchHostInvalidation message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DotnetWatchHostJsonContext.Default.DotnetWatchHostInvalidation);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The AppHost watch notification exceeds the supported size.");
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<DotnetWatchHostInvalidation> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Private Aspire framing: a four-byte little-endian length and one UTF-8 JSON object,
        // for example {"Version":1,"Session":"<generation-id>","Kind":0}. Kind 1 also carries
        // an Instance identity for real host startups. This is not the tool's resource protocol.
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("Invalid AppHost watch notification length.");
        }

        var payload = new byte[count];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, DotnetWatchHostJsonContext.Default.DotnetWatchHostInvalidation)
            ?? throw new InvalidDataException("The AppHost watch notification is empty.");
    }
}

[JsonSerializable(typeof(DotnetWatchHostInvalidation))]
internal sealed partial class DotnetWatchHostJsonContext : JsonSerializerContext;
