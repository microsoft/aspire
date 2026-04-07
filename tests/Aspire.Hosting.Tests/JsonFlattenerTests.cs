// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines.Internal;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class JsonFlattenerTests
{
    [Fact]
    public void FlattenJsonObject_HandlesNestedStructures()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["Azure:SubscriptionId"] = "existing-flat-value",
            ["Azure"] = new JsonObject
            {
                ["Tenant"] = "microsoft.onmicrosoft.com",
                ["Deployments"] = new JsonObject
                {
                    ["MyStorage"] = new JsonObject
                    {
                        ["Id"] = "/subscriptions/123/deployments/MyStorage",
                        ["Parameters"] = "{ \"param\": \"value\" }"
                    }
                }
            }
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Equal("existing-flat-value", result["Azure:SubscriptionId"]!.ToString());
        Assert.Equal("microsoft.onmicrosoft.com", result["Azure:Tenant"]!.ToString());
        Assert.Equal("/subscriptions/123/deployments/MyStorage", result["Azure:Deployments:MyStorage:Id"]!.ToString());
        Assert.Equal("{ \"param\": \"value\" }", result["Azure:Deployments:MyStorage:Parameters"]!.ToString());

        // Verify no nested structures remain
        Assert.False(result.ContainsKey("Azure"), "Should not have nested 'Azure' object");
    }

    [Fact]
    public void FlattenJsonObject_HandlesSimpleFlatStructure()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["Azure:SubscriptionId"] = "07268dd7-4c50-434b-b1ff-67b8164edb41",
            ["Azure:Tenant"] = "microsoft.onmicrosoft.com",
            ["Azure:Location"] = "eastus2"
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("07268dd7-4c50-434b-b1ff-67b8164edb41", result["Azure:SubscriptionId"]!.ToString());
        Assert.Equal("microsoft.onmicrosoft.com", result["Azure:Tenant"]!.ToString());
        Assert.Equal("eastus2", result["Azure:Location"]!.ToString());
    }

    [Fact]
    public void FlattenJsonObject_HandlesEmptyObject()
    {
        // Arrange
        var userSecrets = new JsonObject();

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void FlattenJsonObject_HandlesDeeplyNestedStructures()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["Level1"] = new JsonObject
            {
                ["Level2"] = new JsonObject
                {
                    ["Level3"] = new JsonObject
                    {
                        ["DeepValue"] = "nested-value"
                    }
                }
            }
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Single(result);
        Assert.Equal("nested-value", result["Level1:Level2:Level3:DeepValue"]!.ToString());
    }

    [Fact]
    public void FlattenJsonObject_PreservesNullAndPrimitiveValues()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["StringValue"] = "text",
            ["NumberValue"] = 42,
            ["BoolValue"] = true,
            ["NullValue"] = null,
            ["Nested"] = new JsonObject
            {
                ["InnerString"] = "inner-text",
                ["InnerNull"] = null
            }
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Equal("text", result["StringValue"]!.ToString());
        Assert.Equal("42", result["NumberValue"]!.ToString());
        Assert.Equal("true", result["BoolValue"]!.ToString());
        Assert.Null(result["NullValue"]);
        Assert.Equal("inner-text", result["Nested:InnerString"]!.ToString());
        Assert.Null(result["Nested:InnerNull"]);
    }

    [Fact]
    public void FlattenJsonObject_HandlesArraysWithPrimitiveValues()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["SimpleArray"] = new JsonArray("value1", "value2", "value3"),
            ["NumberArray"] = new JsonArray(1, 2, 3),
            ["MixedArray"] = new JsonArray("text", 42, true, null)
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Equal("value1", result["SimpleArray:0"]!.ToString());
        Assert.Equal("value2", result["SimpleArray:1"]!.ToString());
        Assert.Equal("value3", result["SimpleArray:2"]!.ToString());

        Assert.Equal("1", result["NumberArray:0"]!.ToString());
        Assert.Equal("2", result["NumberArray:1"]!.ToString());
        Assert.Equal("3", result["NumberArray:2"]!.ToString());

        Assert.Equal("text", result["MixedArray:0"]!.ToString());
        Assert.Equal("42", result["MixedArray:1"]!.ToString());
        Assert.Equal("true", result["MixedArray:2"]!.ToString());
        Assert.Null(result["MixedArray:3"]);
    }

    [Fact]
    public void FlattenJsonObject_HandlesArraysWithObjects()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["ObjectArray"] = new JsonArray(
                new JsonObject { ["Name"] = "Item1", ["Value"] = "Value1" },
                new JsonObject { ["Name"] = "Item2", ["Value"] = "Value2" }
            ),
            ["NestedConfig"] = new JsonObject
            {
                ["Items"] = new JsonArray(
                    new JsonObject
                    {
                        ["Id"] = "1",
                        ["Settings"] = new JsonObject { ["Enabled"] = true }
                    }
                )
            }
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Equal("Item1", result["ObjectArray:0:Name"]!.ToString());
        Assert.Equal("Value1", result["ObjectArray:0:Value"]!.ToString());
        Assert.Equal("Item2", result["ObjectArray:1:Name"]!.ToString());
        Assert.Equal("Value2", result["ObjectArray:1:Value"]!.ToString());

        Assert.Equal("1", result["NestedConfig:Items:0:Id"]!.ToString());
        Assert.Equal("true", result["NestedConfig:Items:0:Settings:Enabled"]!.ToString());
    }

    [Fact]
    public void FlattenJsonObject_HandlesEmptyArrays()
    {
        // Arrange
        var userSecrets = new JsonObject
        {
            ["EmptyArray"] = new JsonArray(),
            ["OtherValue"] = "test"
        };

        // Act
        var result = JsonFlattener.FlattenJsonObject(userSecrets);

        // Assert
        Assert.Single(result);
        Assert.Equal("test", result["OtherValue"]!.ToString());
        Assert.False(result.ContainsKey("EmptyArray"));
    }
}
