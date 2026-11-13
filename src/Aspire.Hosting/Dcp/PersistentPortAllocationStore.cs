// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

internal sealed class PersistentPortAllocationStore(
    AppHostLocalStateStore stateStore,
    ILogger<PersistentPortAllocationStore> logger)
{
    private const string StateKey = "portAllocations";
    private const int CurrentVersion = 1;

    public bool IsAvailable => stateStore.IsAvailable;

    public IReadOnlyDictionary<PersistentPortAllocationKey, int> GetAllocations()
    {
        return stateStore.Read(
            StateKey,
            value => TryReadPortAllocations(value, out var allocations) ? allocations : [],
            new Dictionary<PersistentPortAllocationKey, int>());
    }

    public void SetPort(PersistentPortAllocationKey key, int port)
    {
        stateStore.Update(StateKey, value =>
        {
            if (!TryReadPortAllocations(value, out var allocations))
            {
                return value;
            }

            if (allocations.TryGetValue(key, out var existingPort) && existingPort == port)
            {
                return value;
            }

            allocations[key] = port;
            return WritePortAllocations(allocations);
        });
    }

    private bool TryReadPortAllocations(
        JsonNode? value,
        out Dictionary<PersistentPortAllocationKey, int> allocations)
    {
        allocations = [];
        if (value is null)
        {
            return true;
        }

        if (value is not JsonObject state)
        {
            logger.LogWarning("Ignoring invalid {StateKey} AppHost local state value.", StateKey);
            return true;
        }

        if (state["version"] is not JsonValue versionValue ||
            !versionValue.TryGetValue<int>(out var version))
        {
            logger.LogWarning("Ignoring invalid {StateKey} AppHost local state value.", StateKey);
            return true;
        }

        if (version != CurrentVersion)
        {
            logger.LogWarning(
                "Ignoring unsupported {StateKey} AppHost local state value with version {Version}.",
                StateKey,
                version);
            return false;
        }

        if (state["resources"] is not JsonObject resourcesObject)
        {
            return true;
        }

        foreach (var resource in resourcesObject)
        {
            if (resource.Value is not JsonObject endpointsObject)
            {
                continue;
            }

            foreach (var endpoint in endpointsObject)
            {
                if (endpoint.Value is not JsonObject endpointObject)
                {
                    continue;
                }

                if (TryGetPort(endpointObject["port"], out var publicPort))
                {
                    allocations[new PersistentPortAllocationKey(resource.Key, endpoint.Key)] = publicPort;
                }
            }
        }

        return true;

        static bool TryGetPort(JsonNode? node, out int port)
        {
            if (node is JsonValue value &&
                value.TryGetValue(out port) &&
                port is > 0 and <= 65535)
            {
                return true;
            }

            port = default;
            return false;
        }
    }

    private static JsonObject WritePortAllocations(Dictionary<PersistentPortAllocationKey, int> allocations)
    {
        var resourcesObject = new JsonObject();
        foreach (var allocation in allocations
            .OrderBy(static allocation => allocation.Key.ResourceName, StringComparer.Ordinal)
            .ThenBy(static allocation => allocation.Key.EndpointName, StringComparer.Ordinal))
        {
            if (resourcesObject[allocation.Key.ResourceName] is not JsonObject endpointsObject)
            {
                endpointsObject = new JsonObject();
                resourcesObject[allocation.Key.ResourceName] = endpointsObject;
            }

            if (endpointsObject[allocation.Key.EndpointName] is not JsonObject endpointObject)
            {
                endpointObject = new JsonObject();
                endpointsObject[allocation.Key.EndpointName] = endpointObject;
            }

            endpointObject["port"] = allocation.Value;
        }

        return new JsonObject
        {
            ["version"] = CurrentVersion,
            ["resources"] = resourcesObject
        };
    }
}

internal readonly record struct PersistentPortAllocationKey(string ResourceName, string EndpointName)
{
    public bool Equals(PersistentPortAllocationKey other) =>
        StringComparers.ResourceName.Equals(ResourceName, other.ResourceName) &&
        StringComparers.EndpointAnnotationName.Equals(EndpointName, other.EndpointName);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ResourceName, StringComparers.ResourceName);
        hash.Add(EndpointName, StringComparers.EndpointAnnotationName);
        return hash.ToHashCode();
    }
}
