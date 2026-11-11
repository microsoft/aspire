// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Hosting.Native.Cli;

namespace Aspire.Hosting.Native.Auxiliary;

internal enum NativeWaitTarget { Unknown, Up, Healthy, Down }

/// <summary>Parses auxiliary protocol arguments independently of socket and resource lifetimes.</summary>
internal static class NativeAuxiliaryProtocol
{
    public static bool TryParseWaitTarget(string value, out NativeWaitTarget target)
    {
        target = value switch
        {
            "up" => NativeWaitTarget.Up,
            "healthy" => NativeWaitTarget.Healthy,
            "down" => NativeWaitTarget.Down,
            _ => NativeWaitTarget.Unknown
        };

        return target != NativeWaitTarget.Unknown;
    }

    public static T? ReadRequest<T>(NativeCliRequest request, JsonTypeInfo<T> typeInfo, bool required) where T : class
    {
        JsonElement? value = null;
        // StreamJsonRpc sends positional [{...}] or named {"request":{...}} parameters.
        // vscode-jsonrpc may send the request directly. Optional requests can be
        // absent, [], [null], or null; extra positional/named arguments are errors.
        if (request.Params is { ValueKind: not JsonValueKind.Null } parameters)
        {
            if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() <= 1)
            {
                value = parameters.GetArrayLength() == 1 ? parameters[0] : null;
            }
            else if (parameters.ValueKind == JsonValueKind.Object)
            {
                if (parameters.TryGetProperty("request", out var named))
                {
                    if (parameters.EnumerateObject().Count() != 1)
                    {
                        throw new ArgumentException("Expected one request object.");
                    }
                    value = named;
                }
                else
                {
                    value = parameters;
                }
            }
            else
            {
                throw new ArgumentException("Expected one request object.");
            }
        }
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return required ? throw new ArgumentException("A request is required.") : null;
        }

        return value.Value.Deserialize(typeInfo) ?? throw new ArgumentException("A request is required.");
    }

    public static JsonElement[] LegacyArguments(NativeCliRequest request, int maximum)
    {
        if (request.Params is null || request.Params.Value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (request.Params.Value.ValueKind != JsonValueKind.Array || request.Params.Value.GetArrayLength() > maximum)
        {
            throw new ArgumentException("Invalid legacy arguments.");
        }

        return request.Params.Value.EnumerateArray().ToArray();
    }
}
