// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Hosting.Native.Cli;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Runs one backchannel connection over a caller-owned duplex stream.</summary>
internal interface INativeBackchannelRpc
{
    /// <summary>Dispatches concurrent requests and drains connection-owned work before returning.</summary>
    Task ServeAsync(Stream stream,
        Func<NativeCliRequest, CancellationToken, Task<NativeBackchannelReply>> dispatch,
        CancellationToken cancellationToken);
}

/// <summary>Returns copied data or a connection-owned stream, with optional post-flush lifecycle work.</summary>
internal sealed record NativeBackchannelReply(JsonElement Result, IAsyncEnumerable<JsonElement>? Stream = null, Action? AfterResponse = null)
{
    internal static NativeBackchannelReply Value<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(JsonSerializer.SerializeToElement(value, typeInfo));

    internal static NativeBackchannelReply Streaming<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo) =>
        new(default, EncodeAsync(source, typeInfo));

    private static async IAsyncEnumerable<JsonElement> EncodeAsync<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return JsonSerializer.SerializeToElement(value, typeInfo);
        }
    }
}
