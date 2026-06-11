// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Hosting.Azure;

internal static class JsonExtensions
{
    internal static JsonNode Prop(this JsonNode obj, string key)
    {
        var jsonObj = obj.AsObject();

        // Try to get the existing node
        var node = jsonObj[key];
        if (node is not null)
        {
            return node;
        }

        // Create a new node and try to add it
        node = new JsonObject();

        if (!jsonObj.TryAdd(key, node))
        {
            node = jsonObj[key];
            if (node is null)
            {
                throw new InvalidOperationException($"Failed to get or create property '{key}'");
            }
        }

        return node;
    }
}
