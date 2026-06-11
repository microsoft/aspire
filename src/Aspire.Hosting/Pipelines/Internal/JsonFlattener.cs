// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Hosting.Pipelines.Internal;

/// <summary>
/// Provides utility methods for flattening and unflattening JSON objects using colon-separated keys.
/// </summary>
internal static class JsonFlattener
{
    /// <summary>
    /// Flattens a JsonObject using colon-separated keys for configuration compatibility.
    /// Handles both nested objects and arrays with indexed keys.
    /// </summary>
    /// <param name="source">The source JsonObject to flatten.</param>
    /// <returns>A flattened JsonObject.</returns>
    public static JsonObject FlattenJsonObject(JsonObject source)
    {
        var result = new JsonObject();
        FlattenJsonObjectRecursive(source, string.Empty, result);
        return result;
    }

    /// <summary>
    /// Unflattens a JsonObject that uses colon-separated keys back into a nested structure.
    /// Handles both nested objects and arrays with indexed keys.
    /// </summary>
    /// <param name="source">The flattened JsonObject to unflatten.</param>
    /// <returns>An unflattened JsonObject with nested structure.</returns>
    public static JsonObject UnflattenJsonObject(JsonObject source)
    {
        var result = new JsonObject();

        foreach (var kvp in source)
        {
            var keys = kvp.Key.Split(':');
            var current = result;

            for (var i = 0; i < keys.Length - 1; i++)
            {
                var key = keys[i];
                if (!current.TryGetPropertyValue(key, out var existing) || existing is not JsonObject)
                {
                    var newObject = new JsonObject();
                    current[key] = newObject;
                    current = newObject;
                }
                else
                {
                    current = existing.AsObject();
                }
            }

            current[keys[^1]] = kvp.Value?.DeepClone();
        }

        return result;
    }

    private static void FlattenJsonObjectRecursive(JsonObject source, string prefix, JsonObject result)
    {
        foreach (var kvp in source)
        {
            var key = string.IsNullOrEmpty(prefix)
                ? kvp.Key
                : string.IsNullOrEmpty(kvp.Key)
                    ? prefix
                    : $"{prefix}:{kvp.Key}";

            if (kvp.Value is JsonObject nestedObject)
            {
                FlattenJsonObjectRecursive(nestedObject, key, result);
            }
            else if (kvp.Value is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var arrayKey = $"{key}:{i}";
                    if (array[i] is JsonObject arrayObject)
                    {
                        FlattenJsonObjectRecursive(arrayObject, arrayKey, result);
                    }
                    else
                    {
                        result[arrayKey] = array[i]?.DeepClone();
                    }
                }
            }
            else
            {
                result[key] = kvp.Value?.DeepClone();
            }
        }
    }
}
