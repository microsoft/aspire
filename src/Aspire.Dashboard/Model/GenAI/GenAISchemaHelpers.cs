// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Aspire.Dashboard.Model.GenAI;

internal static class GenAISchemaHelpers
{
    private const string TypeNull = "null";
    private const string TypeBoolean = "boolean";
    private const string TypeInteger = "integer";
    private const string TypeNumber = "number";
    private const string TypeString = "string";
    private const string TypeObject = "object";
    private const string TypeArray = "array";

    internal static OpenApiSchema? ParseOpenApiSchema(JsonObject schemaObj)
    {
        var schema = new OpenApiSchema
        {
            Type = ParseTypeValue(schemaObj["type"]),
            Description = schemaObj["description"]?.GetValue<string>()
        };

        // Parse properties
        if (schemaObj["properties"] is JsonObject propsObj)
        {
            schema.Properties = new Dictionary<string, IOpenApiSchema>();
            foreach (var prop in propsObj)
            {
                if (prop.Value is JsonObject propSchemaObj)
                {
                    var parsedSchema = ParseOpenApiSchema(propSchemaObj);
                    if (parsedSchema != null)
                    {
                        schema.Properties[prop.Key] = parsedSchema;
                    }
                }
            }
        }

        // Parse items (for array types)
        if (schemaObj["items"] is JsonObject itemsObj)
        {
            schema.Items = ParseOpenApiSchema(itemsObj);
        }

        // Parse required
        if (schemaObj["required"] is JsonArray requiredArray)
        {
            schema.Required = new HashSet<string>();
            foreach (var item in requiredArray)
            {
                if (item != null)
                {
                    schema.Required.Add(item.GetValue<string>());
                }
            }
        }

        // Parse enum
        if (schemaObj["enum"] is JsonArray enumArray)
        {
            schema.Enum = new List<JsonNode>();
            foreach (var item in enumArray)
            {
                if (item != null)
                {
                    schema.Enum.Add(item);
                }
            }
        }

        return schema;
    }

    internal static JsonSchemaType? ParseTypeValue(JsonNode? typeNode)
    {
        if (typeNode == null)
        {
            return null;
        }

        // Handle both string and array of strings for the "type" field
        // In JSON Schema, "type" can be either:
        // - a string: "string"
        // - an array: ["string", "null"]
        if (typeNode is JsonArray typeArray)
        {
            // When type is an array, OR all the types together since JsonSchemaType is a flags enum
            JsonSchemaType? result = null;
            foreach (var item in typeArray)
            {
                if (item != null)
                {
                    // Safely handle non-string values in the array
                    if (item is JsonValue && item.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    {
                        var typeValue = item.GetValue<string>();
                        if (TryConvertToJsonSchemaType(typeValue, out var schemaType))
                        {
                            result = result.HasValue ? result.Value | schemaType : schemaType;
                        }
                    }
                    // Skip non-string items in the array (e.g., objects, nested arrays)
                }
            }
            return result;
        }

        // Handle string type
        // Check if the node is actually a JsonValue with a string before calling GetValue
        if (typeNode is JsonValue && typeNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            var typeString = typeNode.GetValue<string>();
            return TryConvertToJsonSchemaType(typeString, out var type) ? type : null;
        }

        // If the type field is not a string or array of strings (e.g., it's an object), return null
        // This handles unexpected JSON structures gracefully without throwing an exception
        return null;
    }

    internal static bool TryConvertToJsonSchemaType(string? typeString, out JsonSchemaType schemaType)
    {
        schemaType = typeString switch
        {
            TypeNull => JsonSchemaType.Null,
            TypeBoolean => JsonSchemaType.Boolean,
            TypeInteger => JsonSchemaType.Integer,
            TypeNumber => JsonSchemaType.Number,
            TypeString => JsonSchemaType.String,
            TypeObject => JsonSchemaType.Object,
            TypeArray => JsonSchemaType.Array,
            _ => default
        };

        return schemaType != default;
    }

    internal static IList<string> ConvertTypeToNames(IOpenApiSchema? schema)
    {
        if (schema?.Type == null)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        var type = ResolveNullInSchemaType(schema.Type.Value);

        if (type.HasFlag(JsonSchemaType.Null))
        {
            names.Add(TypeNull);
        }
        if (type.HasFlag(JsonSchemaType.Boolean))
        {
            names.Add(TypeBoolean);
        }
        if (type.HasFlag(JsonSchemaType.Integer))
        {
            names.Add(TypeInteger);
        }
        if (type.HasFlag(JsonSchemaType.Number))
        {
            names.Add(TypeNumber);
        }
        if (type.HasFlag(JsonSchemaType.String))
        {
            names.Add(TypeString);
        }
        if (type.HasFlag(JsonSchemaType.Object))
        {
            names.Add(TypeObject);
        }
        if (type.HasFlag(JsonSchemaType.Array))
        {
            names.Add(GetArrayTypeName(schema.Items));
        }

        return names;

        static JsonSchemaType ResolveNullInSchemaType(JsonSchemaType type)
        {
            // Only remove null if type isn't just null
            return type == JsonSchemaType.Null ? type : type & ~JsonSchemaType.Null;
        }

        static string GetArrayTypeName(IOpenApiSchema? itemsSchema)
        {
            if (itemsSchema?.Type != null)
            {
                // Don't nest arrays.
                var itemType = ResolveNullInSchemaType(itemsSchema.Type.Value);
                if (itemType != JsonSchemaType.Array)
                {
                    var itemTypeNames = ConvertTypeToNames(itemsSchema);
                    // Only return single type arrays for simplicity.
                    if (itemTypeNames.Count == 1)
                    {
                        return $"array<{itemTypeNames[0]}>";
                    }
                }
            }
            return TypeArray;
        }
    }
}
