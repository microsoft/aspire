// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Dashboard.Model.GenAI;
using Microsoft.OpenApi;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class GenAISchemaHelpersTests
{
    [Fact]
    public void ConvertTypeToNames_HandlesVariousTypeCombinations()
    {
        // Test single types
        var stringSchema = new OpenApiSchema { Type = JsonSchemaType.String };
        var typeNames = GenAISchemaHelpers.ConvertTypeToNames(stringSchema);
        Assert.Single(typeNames);
        Assert.Equal("string", typeNames[0]);

        // Test multiple types (nullable string) - null should be excluded from display
        var nullableStringSchema = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(nullableStringSchema);
        Assert.Single(typeNames);
        Assert.Equal("string", typeNames[0]);

        // Test array with items
        var arraySchema = new OpenApiSchema 
        { 
            Type = JsonSchemaType.Array,
            Items = new OpenApiSchema { Type = JsonSchemaType.String }
        };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(arraySchema);
        Assert.Single(typeNames);
        Assert.Equal("array<string>", typeNames[0]);

        // Test nullable array with items - null should be excluded
        var nullableArraySchema = new OpenApiSchema 
        { 
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Items = new OpenApiSchema { Type = JsonSchemaType.Number }
        };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(nullableArraySchema);
        Assert.Single(typeNames);
        Assert.Equal("array<number>", typeNames[0]);

        // Test array without items
        var arrayNoItemsSchema = new OpenApiSchema { Type = JsonSchemaType.Array };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(arrayNoItemsSchema);
        Assert.Single(typeNames);
        Assert.Equal("array", typeNames[0]);

        // Test null schema - should return empty list
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(null);
        Assert.Empty(typeNames);

        // Test schema with no type - should return empty list
        var noTypeSchema = new OpenApiSchema();
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(noTypeSchema);
        Assert.Empty(typeNames);

        // Test schema with only null type - should return "null" since type is only null
        var onlyNullSchema = new OpenApiSchema { Type = JsonSchemaType.Null };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(onlyNullSchema);
        Assert.Single(typeNames);
        Assert.Equal("null", typeNames[0]);

        // Test multiple types without null
        var multiTypeSchema = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Number };
        typeNames = GenAISchemaHelpers.ConvertTypeToNames(multiTypeSchema);
        Assert.Equal(2, typeNames.Count);
        Assert.Contains("number", typeNames);
        Assert.Contains("string", typeNames);
    }

    [Fact]
    public void TryConvertToJsonSchemaType_ValidTypes_ReturnsTrue()
    {
        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("null", out var nullType));
        Assert.Equal(JsonSchemaType.Null, nullType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("boolean", out var boolType));
        Assert.Equal(JsonSchemaType.Boolean, boolType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("integer", out var intType));
        Assert.Equal(JsonSchemaType.Integer, intType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("number", out var numberType));
        Assert.Equal(JsonSchemaType.Number, numberType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("string", out var stringType));
        Assert.Equal(JsonSchemaType.String, stringType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("object", out var objectType));
        Assert.Equal(JsonSchemaType.Object, objectType);

        Assert.True(GenAISchemaHelpers.TryConvertToJsonSchemaType("array", out var arrayType));
        Assert.Equal(JsonSchemaType.Array, arrayType);
    }

    [Fact]
    public void TryConvertToJsonSchemaType_InvalidType_ReturnsFalse()
    {
        Assert.False(GenAISchemaHelpers.TryConvertToJsonSchemaType("invalid", out _));
        Assert.False(GenAISchemaHelpers.TryConvertToJsonSchemaType(null, out _));
        Assert.False(GenAISchemaHelpers.TryConvertToJsonSchemaType("", out _));
    }

    [Fact]
    public void ParseTypeValue_HandlesUnexpectedJsonObject_ReturnsNull()
    {
        // Test that when "type" field is a JsonObject instead of a string, it returns null instead of throwing
        var typeAsObject = JsonNode.Parse("""{"description": "This is an object instead of a string"}""");
        var result = GenAISchemaHelpers.ParseTypeValue(typeAsObject);
        Assert.Null(result);
    }

    [Fact]
    public void ParseTypeValue_HandlesArrayWithNonStringItems_SkipsInvalidItems()
    {
        // Test that when "type" array contains non-string items (e.g., objects), it skips them gracefully
        var typeArrayWithObjects = JsonNode.Parse("""["string", {"invalid": "object"}, "number"]""") as JsonArray;
        var result = GenAISchemaHelpers.ParseTypeValue(typeArrayWithObjects);
        
        // Should have parsed "string" and "number" but skipped the object
        Assert.NotNull(result);
        Assert.True(result!.Value.HasFlag(JsonSchemaType.String));
        Assert.True(result.Value.HasFlag(JsonSchemaType.Number));
        Assert.False(result.Value.HasFlag(JsonSchemaType.Object));
    }

    [Fact]
    public void ParseTypeValue_HandlesArrayWithOnlyInvalidItems_ReturnsNull()
    {
        // Test that when "type" array contains only invalid items, it returns null
        var typeArrayWithOnlyObjects = JsonNode.Parse("""[{"invalid": "object"}, {"another": "object"}]""") as JsonArray;
        var result = GenAISchemaHelpers.ParseTypeValue(typeArrayWithOnlyObjects);
        Assert.Null(result);
    }

    [Fact]
    public void ParseTypeValue_HandlesNullNode_ReturnsNull()
    {
        var result = GenAISchemaHelpers.ParseTypeValue(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseTypeValue_HandlesValidString_ReturnsCorrectType()
    {
        var typeString = JsonNode.Parse("\"string\"");
        var result = GenAISchemaHelpers.ParseTypeValue(typeString);
        Assert.Equal(JsonSchemaType.String, result);
    }

    [Fact]
    public void ParseTypeValue_HandlesValidArray_ReturnsCorrectTypes()
    {
        var typeArray = JsonNode.Parse("""["string", "null"]""") as JsonArray;
        var result = GenAISchemaHelpers.ParseTypeValue(typeArray);
        Assert.NotNull(result);
        Assert.True(result!.Value.HasFlag(JsonSchemaType.String));
        Assert.True(result.Value.HasFlag(JsonSchemaType.Null));
    }

    [Fact]
    public void ParseOpenApiSchema_HandlesUnexpectedTypeObject_ReturnsSchemaWithNullType()
    {
        // Test the full ParseOpenApiSchema method with an unexpected "type" field
        var schemaJson = """
        {
            "properties": {
                "param1": {
                    "type": "string"
                },
                "param2": {
                    "type": {
                        "description": "This is an object instead of a string"
                    }
                }
            }
        }
        """;
        
        var schemaObj = JsonNode.Parse(schemaJson) as JsonObject;
        var schema = GenAISchemaHelpers.ParseOpenApiSchema(schemaObj!);
        
        Assert.NotNull(schema);
        Assert.NotNull(schema!.Properties);
        Assert.Equal(2, schema.Properties.Count);
        
        // param1 should be parsed correctly
        Assert.True(schema.Properties.ContainsKey("param1"));
        var param1 = schema.Properties["param1"] as OpenApiSchema;
        Assert.NotNull(param1);
        Assert.Equal(JsonSchemaType.String, param1!.Type);
        
        // param2 should be parsed but with null type (since type is an object)
        Assert.True(schema.Properties.ContainsKey("param2"));
        var param2 = schema.Properties["param2"] as OpenApiSchema;
        Assert.NotNull(param2);
        Assert.Null(param2!.Type);
    }
}
