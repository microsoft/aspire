// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Manages registration and lookup of ATS handles for capability dispatch.
/// Handles are opaque typed references with IDs in the format: {typeId}:{instanceId}
/// </summary>
internal sealed class HandleRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HandleEntry> _handles = new();
    private long _idCounter;

    /// <summary>
    /// Represents a registered handle entry.
    /// </summary>
    private sealed class HandleEntry
    {
        public required object Object { get; init; }
        public required string TypeId { get; init; }
        public required long InstanceId { get; init; }
    }

    /// <summary>
    /// Registers an object as a handle with the specified ATS type ID.
    /// </summary>
    /// <param name="obj">The object to register.</param>
    /// <param name="typeId">The ATS type ID (e.g., "aspire.redis/RedisBuilder").</param>
    /// <returns>The handle ID (just the instance number).</returns>
    public string Register(object obj, string typeId)
    {
        var instanceId = Interlocked.Increment(ref _idCounter);
        var handleId = instanceId.ToString(CultureInfo.InvariantCulture);

        _handles[handleId] = new HandleEntry
        {
            Object = obj,
            TypeId = typeId,
            InstanceId = instanceId
        };

        return handleId;
    }

    /// <summary>
    /// Tries to get a handle entry by its ID.
    /// </summary>
    /// <param name="handleId">The handle ID.</param>
    /// <param name="obj">The underlying object if found.</param>
    /// <param name="typeId">The ATS type ID if found.</param>
    /// <returns>True if the handle was found, false otherwise.</returns>
    public bool TryGet(string handleId, out object? obj, out string? typeId)
    {
        if (_handles.TryGetValue(handleId, out var entry))
        {
            obj = entry.Object;
            typeId = entry.TypeId;
            return true;
        }

        obj = null;
        typeId = null;
        return false;
    }

    /// <summary>
    /// Gets the underlying object for a handle.
    /// </summary>
    /// <param name="handleId">The handle ID.</param>
    /// <returns>The underlying object.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handle is not found.</exception>
    public object GetObject(string handleId)
    {
        if (!_handles.TryGetValue(handleId, out var entry))
        {
            throw new InvalidOperationException($"Handle '{handleId}' not found in registry");
        }
        return entry.Object;
    }

    /// <summary>
    /// Gets the underlying object for a handle, cast to the specified type.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="handleId">The handle ID.</param>
    /// <returns>The underlying object.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handle is not found or type doesn't match.</exception>
    public T GetObject<T>(string handleId) where T : class
    {
        var obj = GetObject(handleId);
        if (obj is not T typed)
        {
            throw new InvalidOperationException(
                $"Handle '{handleId}' contains {obj.GetType().FullName}, expected {typeof(T).FullName}");
        }
        return typed;
    }

    /// <summary>
    /// Gets the ATS type ID for a handle.
    /// </summary>
    /// <param name="handleId">The handle ID.</param>
    /// <returns>The ATS type ID.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handle is not found.</exception>
    public string GetTypeId(string handleId)
    {
        if (!_handles.TryGetValue(handleId, out var entry))
        {
            throw new InvalidOperationException($"Handle '{handleId}' not found in registry");
        }
        return entry.TypeId;
    }

    /// <summary>
    /// Checks if a handle exists in the registry.
    /// </summary>
    /// <param name="handleId">The handle ID.</param>
    /// <returns>True if the handle exists, false otherwise.</returns>
    public bool Contains(string handleId)
    {
        return _handles.ContainsKey(handleId);
    }

    /// <summary>
    /// Unregisters a handle from the registry.
    /// </summary>
    /// <param name="handleId">The handle ID to unregister.</param>
    /// <returns>True if the handle was removed, false if it wasn't found.</returns>
    public bool Unregister(string handleId)
    {
        return _handles.TryRemove(handleId, out _);
    }

    /// <summary>
    /// Marshals an object to a JSON handle reference.
    /// </summary>
    /// <param name="obj">The object to marshal.</param>
    /// <param name="typeId">The ATS type ID.</param>
    /// <returns>A JsonObject containing the handle reference.</returns>
    public JsonObject Marshal(object obj, string typeId)
    {
        var handleId = Register(obj, typeId);
        return new JsonObject
        {
            ["$handle"] = handleId,
            ["$type"] = typeId
        };
    }

    /// <summary>
    /// Gets the count of registered handles.
    /// </summary>
    public int Count => _handles.Count;

    /// <summary>
    /// Disposes all disposable objects in the registry and clears it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var entries = _handles.Values.ToList();
        _handles.Clear();

        foreach (var entry in entries)
        {
            if (entry.Object is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (entry.Object is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

/// <summary>
/// Reference to an ATS handle. Used when passing handles as arguments.
/// JSON shape: { "$handle": "42", "$type": "Aspire.Hosting.Redis/..." }
/// </summary>
internal sealed class HandleRef
{
    /// <summary>
    /// The handle identifier.
    /// </summary>
    public required string HandleId { get; init; }

    /// <summary>
    /// Creates a HandleRef from a JSON node if it contains a $handle property.
    /// </summary>
    public static HandleRef? FromJsonNode(JsonNode? node)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue("$handle", out var handleNode))
        {
            var handleId = handleNode?.GetValue<string>();
            if (!string.IsNullOrEmpty(handleId))
            {
                return new HandleRef { HandleId = handleId };
            }
        }
        return null;
    }

    /// <summary>
    /// Checks if a JSON node is a handle reference.
    /// </summary>
    public static bool IsHandleRef(JsonNode? node)
    {
        return node is JsonObject obj && obj.ContainsKey("$handle");
    }
}
