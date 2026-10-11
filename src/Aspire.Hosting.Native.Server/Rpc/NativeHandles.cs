// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Owns opaque, typed capability handles within one authenticated connection.</summary>
internal sealed class NativeHandles(int capacity) : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _identities = new(ReferenceEqualityComparer.Instance);

    public int Count => _entries.Count;

    public void EnsureCapacity()
    {
        if (_entries.Count >= capacity)
        {
            throw new RpcFault("HANDLE_LIMIT", "The connection handle limit was reached.");
        }
    }

    public JsonObject Add(object value, string typeId)
    {
        if (!_identities.TryGetValue(value, out var id))
        {
            if (_entries.Count >= capacity)
            {
                // A newly constructed session has no handle yet. Release it
                // rather than leaking ownership when publication is rejected.
                (value as ICapabilityOwner)?.Close();
                throw new RpcFault("HANDLE_LIMIT", "The connection handle limit was reached.");
            }
            id = $"{typeId}:{RandomNumberGenerator.GetHexString(32, lowercase: true)}";
            _entries.Add(id, new Entry(value, typeId));
            _identities.Add(value, id);
        }

        return new JsonObject { ["$handle"] = id, ["$type"] = typeId };
    }

    public T Get<T>(JsonNode? value, string expectedTypeId) where T : class
    {
        if (value is not JsonObject reference)
        {
            throw new RpcFault("INVALID_HANDLE", "A capability handle is required.");
        }
        RpcArguments.Validate(reference, ["$handle", "$type"]);
        var id = RpcArguments.String(reference["$handle"]);
        var typeId = RpcArguments.String(reference["$type"]);
        if (!_entries.TryGetValue(id, out var entry) || entry.Value is ICapabilityLifetime { IsRevoked: true })
        {
            throw new RpcFault("HANDLE_NOT_FOUND", "The capability handle is unknown or retired.");
        }
        if (typeId != expectedTypeId || entry.TypeId != expectedTypeId || entry.Value is not T typed)
        {
            throw new RpcFault("TYPE_MISMATCH", "The capability handle is not applicable.");
        }

        return typed;
    }

    public void RemoveRevoked()
    {
        foreach (var (id, entry) in _entries.ToArray())
        {
            if (entry.Value is ICapabilityLifetime { IsRevoked: true })
            {
                _entries.Remove(id);
                _identities.Remove(entry.Value);
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            (entry.Value as ICapabilityOwner)?.Close();
        }
        _entries.Clear();
        _identities.Clear();
    }

    private sealed record Entry(object Value, string TypeId);
}
