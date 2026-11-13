// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Rpc;

internal enum NativeProviderTypeCategory { Primitive, Handle, Callback, Unsupported }
internal sealed record NativeProviderMetadataType(string TypeId, NativeProviderTypeCategory Category);
internal sealed record NativeProviderMetadataCallback(NativeProviderMetadataType[] Parameters, NativeProviderMetadataType ReturnType);
internal sealed record NativeProviderMetadataParameter(string Name, NativeProviderMetadataType Type, bool IsOptional, bool IsNullable,
    NativeProviderMetadataCallback? Callback);
internal sealed record NativeProviderMetadataCapability(string Id, string Method, NativeProviderMetadataParameter[] Parameters,
    NativeProviderMetadataType ReturnType, string? TargetTypeId, string? TargetParameterName, string? LifecyclePhase, JsonObject Wire);
internal sealed record NativeProviderCoreContract(IReadOnlySet<string> Capabilities, IReadOnlySet<string> Handles);

/// <summary>Reads the case-sensitive provider wire schema once, before semantic projection and authority resolution.</summary>
internal static class NativeProviderMetadata
{
    public static NativeProviderCoreContract ReadCoreContract(JsonObject contract)
    {
        // The embedded ATS contract uses {"Capabilities":[{"CapabilityId":"..."}],
        // "HandleTypes":[{"AtsTypeId":"..."}]}, unlike the portable provider envelope.
        return new(
            contract["Capabilities"]!.AsArray().Select(value => value!["CapabilityId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
            contract["HandleTypes"]!.AsArray().Select(value => value!["AtsTypeId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
    }

    public static NativeProviderMetadataCapability[] Read(JsonNode? manifest)
    {
        // {"protocolVersion":2,"capabilities":[{"id":"package/method","method":"method",
        // "parameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
        // "returnType":{"typeId":"void","category":"Primitive"}}]}.
        // Categories and primitive IDs are exact protocol identifiers, not CLR names.
        if (manifest is not JsonObject envelope || envelope["protocolVersion"] is not JsonValue version ||
            !version.TryGetValue<int>(out var number) || number != 2 ||
            envelope["capabilities"] is not JsonArray { Count: > 0 and <= 128 } capabilities)
        {
            throw RpcFault.ProviderMetadataInvalid("Provider metadata requires protocol version 2 and bounded capabilities.");
        }
        var result = new NativeProviderMetadataCapability[capabilities.Count];
        for (var index = 0; index < result.Length; index++)
        {
            if (capabilities[index] is not JsonObject metadata || metadata["id"] is not JsonValue ||
                metadata["parameters"] is not JsonArray { Count: <= 32 } parameters ||
                metadata["returnType"] is not JsonObject returnType)
            {
                throw RpcFault.ProviderMetadataInvalid("Provider capability projection is incomplete.");
            }
            result[index] = new(RpcArguments.String(metadata["id"]), RpcArguments.String(metadata["method"]),
                parameters.Select(ReadParameter).ToArray(), ReadType(returnType), OptionalString(metadata, "targetTypeId"),
                OptionalString(metadata, "targetParameterName"), OptionalString(metadata, "lifecyclePhase"), metadata);
        }

        return result;
    }

    private static NativeProviderMetadataParameter ReadParameter(JsonNode? parameter)
    {
        if (parameter is not JsonObject argument || argument["type"] is not JsonObject type)
        {
            throw RpcFault.ProviderMetadataInvalid("Provider arguments require distinct required names and types.");
        }
        var name = RpcArguments.String(argument["name"]);
        var optional = ReadFlag(argument, "isOptional");
        var nullable = ReadFlag(argument, "isNullable");
        if (!ReadFlag(argument, "isCallback"))
        {
            return new(name, ReadType(type), optional, nullable, null);
        }
        if (type["category"] is not JsonValue category || !category.TryGetValue<string>(out var categoryName) || categoryName != "Callback" ||
            argument["callbackParameters"] is not JsonArray { Count: <= 16 } callbackParameters ||
            argument["callbackReturnType"] is not JsonObject callbackReturn)
        {
            throw RpcFault.ProviderMetadataInvalid("Callback projection is incomplete.");
        }
        var callbackTypes = new NativeProviderMetadataType[callbackParameters.Count];
        for (var index = 0; index < callbackTypes.Length; index++)
        {
            if (callbackParameters[index] is not JsonObject projectedParameter || projectedParameter["type"] is not JsonObject projectedType)
            {
                throw RpcFault.ProviderMetadataInvalid("Callback projection is incomplete.");
            }
            callbackTypes[index] = ReadType(projectedType);
        }

        return new(name, new("callback", NativeProviderTypeCategory.Callback), optional, nullable, new(callbackTypes, ReadType(callbackReturn)));
    }

    private static NativeProviderMetadataType ReadType(JsonObject type) =>
        new(RpcArguments.String(type["typeId"]), RpcArguments.String(type["category"]) switch
        {
            "Primitive" => NativeProviderTypeCategory.Primitive,
            "Handle" => NativeProviderTypeCategory.Handle,
            "Callback" => NativeProviderTypeCategory.Callback,
            _ => NativeProviderTypeCategory.Unsupported
        });

    private static string? OptionalString(JsonObject metadata, string name) =>
        metadata[name] is { } value ? RpcArguments.String(value) : null;

    private static bool ReadFlag(JsonObject metadata, string name)
    {
        if (metadata[name] is null)
        {
            return false;
        }
        if (metadata[name] is JsonValue value && value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        throw RpcFault.ProviderMetadataInvalid("Provider parameter flags must be Boolean values.");
    }
}
