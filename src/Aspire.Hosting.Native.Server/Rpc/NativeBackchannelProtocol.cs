// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Native.Cli;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Parses and writes backchannel wire shapes independently of request and cursor lifetimes.</summary>
internal static class NativeBackchannelProtocol
{
    public static JsonElement NullResult { get; } =
        JsonSerializer.SerializeToElement((string?)null, NativeCliJsonContext.Default.String);

    public static NativeCliRequest ParseRequest(JsonElement envelope)
    {
        var request = envelope.Deserialize(NativeCliJsonContext.Default.NativeCliRequest)
            ?? throw new InvalidDataException("Invalid backchannel request.");
        if (request.Jsonrpc != "2.0" || request.Id is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number) })
        {
            throw new InvalidDataException("Invalid backchannel envelope.");
        }

        return request;
    }

    public static string ReadCancellationId(NativeCliRequest request)
    {
        var cancellation = request.Params?.Deserialize(NativeCliJsonContext.Default.NativeCliCancellation)
            ?? throw new InvalidDataException("Cancellation requires a request ID.");
        if (cancellation.Id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new InvalidDataException("Cancellation requires a string or number request ID.");
        }

        return cancellation.Id.GetRawText();
    }

    public static long ReadEnumeratorToken(NativeCliRequest request)
    {
        // StreamJsonRpc enumeration: next/abort take [<integer-token>].
        // Reject nonnumbers before TryGetInt64, which throws for other kinds.
        if (request.Params is not { ValueKind: JsonValueKind.Array } arguments ||
            arguments.GetArrayLength() != 1 || arguments[0].ValueKind != JsonValueKind.Number ||
            !arguments[0].TryGetInt64(out var token))
        {
            throw new ArgumentException("An enumerator requires one integer token.");
        }

        return token;
    }

    public static JsonElement StreamDescriptor(long token) =>
        JsonSerializer.SerializeToElement(new NativeCliStreamDescriptor(token), NativeCliJsonContext.Default.NativeCliStreamDescriptor);

    public static JsonElement EnumeratorResult(NativeCliEnumeratorResult result) =>
        JsonSerializer.SerializeToElement(result, NativeCliJsonContext.Default.NativeCliEnumeratorResult);

    public static byte[] EncodeResponse(JsonElement id, JsonElement result, NativeCliError? error) =>
        JsonSerializer.SerializeToUtf8Bytes(new NativeCliResponse
        {
            Id = id,
            Result = error is null ? result : null,
            Error = error
        }, NativeCliJsonContext.Default.NativeCliResponse);
}
