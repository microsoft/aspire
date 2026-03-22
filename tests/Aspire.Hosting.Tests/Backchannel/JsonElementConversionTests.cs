// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Tests for JsonElement to object conversion used in MCP tool calls.
/// </summary>
[Trait("Partition", "4")]
public class JsonElementConversionTests
{
    // Access the private ConvertJsonElementToObject method via reflection for testing
    private static readonly MethodInfo s_convertMethod = typeof(AuxiliaryBackchannelRpcTarget)
        .GetMethod("ConvertJsonElementToObject", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object? ConvertJsonElementToObject(JsonElement element)
    {
        return s_convertMethod.Invoke(null, [element]);
    }

    [Fact]
    public void ConvertJsonElement_String_ReturnsString()
    {
        var json = JsonDocument.Parse("\"hello\"");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.Equal("hello", result);
    }

    [Fact]
    public void ConvertJsonElement_Integer_ReturnsInt()
    {
        var json = JsonDocument.Parse("42");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.IsType<int>(result);
        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public void ConvertJsonElement_Float_ReturnsDouble()
    {
        var json = JsonDocument.Parse("3.14");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.Equal(3.14, result);
    }

    [Fact]
    public void ConvertJsonElement_True_ReturnsBoolTrue()
    {
        var json = JsonDocument.Parse("true");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertJsonElement_False_ReturnsBoolFalse()
    {
        var json = JsonDocument.Parse("false");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertJsonElement_Null_ReturnsNull()
    {
        var json = JsonDocument.Parse("null");
        var result = ConvertJsonElementToObject(json.RootElement);

        Assert.Null(result);
    }

    [Fact]
    public void ConvertJsonElement_Array_ReturnsObjectArray()
    {
        var json = JsonDocument.Parse("[1, 2, 3]");
        var result = ConvertJsonElementToObject(json.RootElement);

        var array = Assert.IsType<object?[]>(result);
        Assert.Equal(3, array.Length);
        Assert.Equal(1, array[0]);
        Assert.Equal(2, array[1]);
        Assert.Equal(3, array[2]);
    }

    [Fact]
    public void ConvertJsonElement_Object_ReturnsDictionary()
    {
        var json = JsonDocument.Parse("""{"name": "test", "value": 123}""");
        var result = ConvertJsonElementToObject(json.RootElement);

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("test", dict["name"]);
        Assert.Equal(123, dict["value"]);
    }

    [Fact]
    public void ConvertJsonElement_NestedObject_ReturnsNestedDictionary()
    {
        var json = JsonDocument.Parse("""{"outer": {"inner": "value"}}""");
        var result = ConvertJsonElementToObject(json.RootElement);

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var nested = Assert.IsType<Dictionary<string, object?>>(dict["outer"]);
        Assert.Equal("value", nested["inner"]);
    }

    [Fact]
    public void ConvertJsonElement_MixedArray_ReturnsCorrectTypes()
    {
        var json = JsonDocument.Parse("""["text", 42, true, null]""");
        var result = ConvertJsonElementToObject(json.RootElement);

        var array = Assert.IsType<object?[]>(result);
        Assert.Equal(4, array.Length);
        Assert.Equal("text", array[0]);
        Assert.Equal(42, array[1]);
        Assert.Equal(true, array[2]);
        Assert.Null(array[3]);
    }

    [Fact]
    public void ConvertJsonElement_ComplexMcpArguments_ConvertsCorrectly()
    {
        // Simulate typical MCP tool arguments
        var json = JsonDocument.Parse("""
        {
            "query": "SELECT * FROM users",
            "limit": 100,
            "includeDeleted": false,
            "filters": ["active", "verified"],
            "options": {
                "timeout": 30,
                "retries": 3
            }
        }
        """);

        var result = ConvertJsonElementToObject(json.RootElement);

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("SELECT * FROM users", dict["query"]);
        Assert.Equal(100, dict["limit"]);
        Assert.Equal(false, dict["includeDeleted"]);

        var filters = Assert.IsType<object?[]>(dict["filters"]);
        Assert.Equal(2, filters.Length);
        Assert.Equal("active", filters[0]);
        Assert.Equal("verified", filters[1]);

        var options = Assert.IsType<Dictionary<string, object?>>(dict["options"]);
        Assert.Equal(30, options["timeout"]);
        Assert.Equal(3, options["retries"]);
    }
}
