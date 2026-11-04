// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Validates the exact argument shape of statically generated capability calls.</summary>
internal static class RpcArguments
{
    public static void Validate(JsonObject arguments, string[] names)
    {
        if (arguments.Count != names.Length || names.Any(name => !arguments.ContainsKey(name)))
        {
            throw RpcFault.InvalidArgument("The capability argument shape is invalid.");
        }
    }

    public static string String(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw RpcFault.InvalidArgument("A string argument is required.");
    }

    public static bool Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var result)
        ? result : throw RpcFault.InvalidArgument("A boolean argument is required.");

    public static string[] Strings(JsonNode? value) => value is JsonArray array
        ? array.Select(String).ToArray()
        : throw RpcFault.InvalidArgument("A string array argument is required.");

    public static object Union(JsonNode? value, NativeHandles handles, string[] typeIds)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            return text;
        }
        if (value is JsonObject reference && typeIds.Contains(String(reference["$type"]), StringComparer.Ordinal))
        {
            return handles.ReadReference(reference);
        }

        throw RpcFault.InvalidArgument("A string or an applicable symbolic handle is required.");
    }

    public static int Int32(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<int>(out var result)
        ? result : throw RpcFault.InvalidArgument("An integer argument is required.");

    public static long Int64(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<long>(out var result)
        ? result : throw RpcFault.InvalidArgument("An integer argument is required.");

    public static T Dto<T>(JsonNode? value, JsonTypeInfo<T> typeInfo) where T : class =>
        value is JsonObject ? value.Deserialize(typeInfo)
            ?? throw RpcFault.InvalidArgument("An object argument is required.")
            : throw RpcFault.InvalidArgument("An object argument is required.");
}
