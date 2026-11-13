// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Creates and applies the subset of RFC 6902 JSON Patch operations used by DCP resources.
/// Paths use the JSON Pointer encoding defined by <see href="https://www.rfc-editor.org/rfc/rfc6901">RFC 6901</see>.
/// </summary>
internal static class JsonPatch
{
    internal static JsonArray Create(JsonNode? current, JsonNode? changed)
    {
        var operations = new JsonArray();
        AddOperations(current, changed, string.Empty, operations);

        return operations;
    }

    internal static JsonNode? Apply(JsonNode? current, JsonArray patch)
    {
        var result = current?.DeepClone();

        foreach (var operationNode in patch)
        {
            if (operationNode is not JsonObject operation ||
                operation["op"] is not JsonValue operationValue ||
                operation["path"] is not JsonValue pathValue ||
                !operationValue.TryGetValue<string>(out var operationName) ||
                !pathValue.TryGetValue<string>(out var path))
            {
                throw new JsonException("A JSON Patch operation must contain string 'op' and 'path' properties.");
            }

            var segments = ParsePath(path);
            var hasValue = operation.TryGetPropertyValue("value", out var value);
            result = ApplyOperation(result, operationName, segments, hasValue, value);
        }

        return result;
    }

    private static void AddOperations(JsonNode? current, JsonNode? changed, string path, JsonArray operations)
    {
        if (JsonNode.DeepEquals(current, changed))
        {
            return;
        }

        if (current is JsonObject currentObject && changed is JsonObject changedObject)
        {
            foreach (var property in currentObject)
            {
                if (!changedObject.ContainsKey(property.Key))
                {
                    operations.Add(CreateOperation("remove", AppendPath(path, property.Key)));
                }
            }

            foreach (var property in changedObject)
            {
                var propertyPath = AppendPath(path, property.Key);
                if (currentObject.TryGetPropertyValue(property.Key, out var currentValue))
                {
                    AddOperations(currentValue, property.Value, propertyPath, operations);
                }
                else
                {
                    operations.Add(CreateOperation("add", propertyPath, property.Value));
                }
            }

            return;
        }

        if (current is JsonArray currentArray && changed is JsonArray changedArray)
        {
            var commonLength = Math.Min(currentArray.Count, changedArray.Count);
            for (var index = 0; index < commonLength; index++)
            {
                AddOperations(currentArray[index], changedArray[index], AppendPath(path, index), operations);
            }

            for (var index = currentArray.Count - 1; index >= changedArray.Count; index--)
            {
                operations.Add(CreateOperation("remove", AppendPath(path, index)));
            }

            for (var index = currentArray.Count; index < changedArray.Count; index++)
            {
                operations.Add(CreateOperation("add", AppendPath(path, index), changedArray[index]));
            }

            return;
        }

        operations.Add(CreateOperation("replace", path, changed));
    }

    private static JsonObject CreateOperation(string operation, string path, JsonNode? value = null)
    {
        var result = new JsonObject
        {
            ["op"] = operation,
            ["path"] = path,
        };

        if (operation is not "remove")
        {
            result["value"] = value?.DeepClone();
        }

        return result;
    }

    private static string AppendPath(string path, string segment)
    {
        return $"{path}/{segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
    }

    private static string AppendPath(string path, int index)
    {
        return $"{path}/{index.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string[] ParsePath(string path)
    {
        if (path.Length == 0)
        {
            return [];
        }

        if (path[0] != '/')
        {
            throw new JsonException($"JSON Pointer path '{path}' must be empty or start with '/'.");
        }

        return path[1..].Split('/').Select(UnescapePathSegment).ToArray();
    }

    private static string UnescapePathSegment(string segment)
    {
        if (!segment.Contains('~', StringComparison.Ordinal))
        {
            return segment;
        }

        var result = new StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            var character = segment[index];
            if (character != '~')
            {
                result.Append(character);
                continue;
            }

            if (++index >= segment.Length)
            {
                throw new JsonException($"JSON Pointer segment '{segment}' ends with an incomplete escape.");
            }

            result.Append(segment[index] switch
            {
                '0' => '~',
                '1' => '/',
                _ => throw new JsonException($"JSON Pointer segment '{segment}' contains an invalid escape."),
            });
        }

        return result.ToString();
    }

    private static JsonNode? ApplyOperation(JsonNode? current, string operation, string[] segments, bool hasValue, JsonNode? value)
    {
        if (operation is not ("add" or "remove" or "replace"))
        {
            throw new JsonException($"JSON Patch operation '{operation}' is not supported.");
        }

        if (operation is not "remove" && !hasValue)
        {
            throw new JsonException($"JSON Patch operation '{operation}' requires a 'value' property.");
        }

        if (segments.Length == 0)
        {
            return operation switch
            {
                "remove" => null,
                _ => value?.DeepClone(),
            };
        }

        var parent = GetParent(current, segments);
        var finalSegment = segments[^1];

        switch (parent)
        {
            case JsonObject jsonObject:
                ApplyToObject(jsonObject, operation, finalSegment, value);
                break;
            case JsonArray jsonArray:
                ApplyToArray(jsonArray, operation, finalSegment, value);
                break;
            default:
                throw new JsonException("A JSON Patch path can only traverse objects and arrays.");
        }

        return current;
    }

    private static JsonNode GetParent(JsonNode? current, string[] segments)
    {
        var parent = current ?? throw new JsonException("A JSON Patch path cannot traverse a null value.");

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            parent = parent switch
            {
                JsonObject jsonObject when jsonObject.TryGetPropertyValue(segment, out var child) && child is not null => child,
                JsonArray jsonArray => jsonArray[ParseArrayIndex(segment, jsonArray.Count, allowEnd: false)]
                    ?? throw new JsonException($"JSON Patch path segment '{segment}' refers to a null value."),
                _ => throw new JsonException($"JSON Patch path segment '{segment}' does not exist."),
            };
        }

        return parent;
    }

    private static void ApplyToObject(JsonObject target, string operation, string propertyName, JsonNode? value)
    {
        switch (operation)
        {
            case "add":
                target[propertyName] = value?.DeepClone();
                break;
            case "remove":
                if (!target.Remove(propertyName))
                {
                    throw new JsonException($"JSON Patch remove path refers to missing property '{propertyName}'.");
                }
                break;
            case "replace":
                if (!target.ContainsKey(propertyName))
                {
                    throw new JsonException($"JSON Patch replace path refers to missing property '{propertyName}'.");
                }
                target[propertyName] = value?.DeepClone();
                break;
        }
    }

    private static void ApplyToArray(JsonArray target, string operation, string indexText, JsonNode? value)
    {
        if (operation == "add" && indexText == "-")
        {
            target.Add(value?.DeepClone());
            return;
        }

        var index = ParseArrayIndex(indexText, target.Count, allowEnd: operation == "add");
        switch (operation)
        {
            case "add":
                target.Insert(index, value?.DeepClone());
                break;
            case "remove":
                target.RemoveAt(index);
                break;
            case "replace":
                target[index] = value?.DeepClone();
                break;
        }
    }

    private static int ParseArrayIndex(string indexText, int count, bool allowEnd)
    {
        if (indexText.Length == 0 ||
            (indexText.Length > 1 && indexText[0] == '0') ||
            !indexText.All(char.IsAsciiDigit) ||
            !int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index < 0 ||
            index > count ||
            (!allowEnd && index == count))
        {
            throw new JsonException($"JSON Patch array index '{indexText}' is invalid for an array with {count} elements.");
        }

        return index;
    }
}
