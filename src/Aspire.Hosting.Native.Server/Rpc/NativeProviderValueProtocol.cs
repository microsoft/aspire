// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Validates invocation values against projected types without resolving handle authority.</summary>
internal static class NativeProviderValueProtocol
{
    public static void ValidatePrimitive(JsonNode? value, NativeProviderType type)
    {
        switch (type.Kind)
        {
            case NativeProviderValueKind.Void when value is null: return;
            case NativeProviderValueKind.String: _ = RpcArguments.String(value); return;
            case NativeProviderValueKind.Boolean: _ = RpcArguments.Boolean(value); return;
            case NativeProviderValueKind.Int32: _ = RpcArguments.Int32(value); return;
            case NativeProviderValueKind.Int64: _ = RpcArguments.Int64(value); return;
            default: throw RpcFault.TypeMismatch("The provider value does not match its projection.");
        }
    }

    public static NativeProviderHandle ParseReturnedHandle(JsonNode? value, NativeProviderType type, IReadOnlySet<string> coreHandles)
    {
        // Core handles are {"$handle":"id","$type":"core/Type"}; provider-owned
        // handles also require "$lifetime" containing delegated native declaration authority.
        var handle = value as JsonObject ?? throw RpcFault.InvalidHandle("The provider did not return a typed handle.");
        var core = coreHandles.Contains(type.TypeId);
        RpcArguments.Validate(handle, core ? ["$handle", "$type"] : ["$handle", "$type", "$lifetime"]);
        var id = RpcArguments.String(handle["$handle"]);
        if (id.Length is 0 or > 512 || RpcArguments.String(handle["$type"]) != type.TypeId)
        {
            throw RpcFault.TypeMismatch("The provider returned an invalid resource handle.");
        }
        if (!core && handle["$lifetime"] is not JsonObject)
        {
            throw RpcFault.InvalidHandle("Integration resource lifetime must be delegated by its caller.");
        }

        return new(id, handle, handle["$lifetime"] as JsonObject);
    }
}
