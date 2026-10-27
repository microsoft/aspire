// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Native.Client;

/// <summary>Base identity for generated connection-scoped native capabilities.</summary>
/// <remarks>Capabilities cannot be constructed from a resource name or transferred between client connections.</remarks>
public abstract class NativeRemoteObject
{
    /// <summary>Gets the connection that owns this generated capability.</summary>
    public NativeClient Client { get; }
    /// <summary>Gets the opaque wire identity for generated projection infrastructure.</summary>
    public JsonElement Handle { get; }

    /// <summary>Validates an opaque identity against its offline-generated type contract.</summary>
    protected NativeRemoteObject(NativeClient client, JsonElement handle, string expectedType)
    {
        if (handle.ValueKind != JsonValueKind.Object ||
            handle.EnumerateObject().Count() != 2 ||
            !handle.TryGetProperty("$type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != expectedType ||
            !handle.TryGetProperty("$handle", out var identity) || identity.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(identity.GetString()))
        {
            throw new InvalidDataException("The native capability handle does not match the generated contract.");
        }
        Client = client;
        Handle = handle.Clone();
    }
}
