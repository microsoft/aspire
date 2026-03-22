// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class ReferenceExpressionRefTests
{
    [Fact]
    public void FromJsonNode_WithValidExpression_ReturnsRef()
    {
        // Arrange
        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["format"] = "redis://localhost:6379"
            }
        };

        // Act
        var exprRef = ReferenceExpressionRef.FromJsonNode(json);

        // Assert
        Assert.NotNull(exprRef);
        Assert.Equal("redis://localhost:6379", exprRef.Format);
        Assert.Null(exprRef.ValueProviders);
    }

    [Fact]
    public void FromJsonNode_WithValueProviders_ReturnsRef()
    {
        // Arrange
        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["format"] = "redis://{0}:{1}",
                ["valueProviders"] = new JsonArray
                {
                    new JsonObject { ["$handle"] = "aspire/EndpointReference:1" },
                    new JsonObject { ["$handle"] = "aspire/EndpointReference:2" }
                }
            }
        };

        // Act
        var exprRef = ReferenceExpressionRef.FromJsonNode(json);

        // Assert
        Assert.NotNull(exprRef);
        Assert.Equal("redis://{0}:{1}", exprRef.Format);
        Assert.NotNull(exprRef.ValueProviders);
        Assert.Equal(2, exprRef.ValueProviders.Length);
    }

    [Fact]
    public void FromJsonNode_WithoutExpr_ReturnsNull()
    {
        // Arrange
        var json = new JsonObject
        {
            ["format"] = "redis://localhost:6379"
        };

        // Act
        var exprRef = ReferenceExpressionRef.FromJsonNode(json);

        // Assert
        Assert.Null(exprRef);
    }

    [Fact]
    public void FromJsonNode_WithNullNode_ReturnsNull()
    {
        // Act
        var exprRef = ReferenceExpressionRef.FromJsonNode(null);

        // Assert
        Assert.Null(exprRef);
    }

    [Fact]
    public void IsReferenceExpressionRef_WithExprProperty_ReturnsTrue()
    {
        // Arrange
        var json = new JsonObject
        {
            ["$expr"] = new JsonObject { ["format"] = "test" }
        };

        // Act & Assert
        Assert.True(ReferenceExpressionRef.IsReferenceExpressionRef(json));
    }

    [Fact]
    public void IsReferenceExpressionRef_WithoutExprProperty_ReturnsFalse()
    {
        // Arrange
        var json = new JsonObject { ["format"] = "test" };

        // Act & Assert
        Assert.False(ReferenceExpressionRef.IsReferenceExpressionRef(json));
    }

    [Fact]
    public void ToReferenceExpression_WithLiteralOnly_CreatesExpression()
    {
        // Arrange
        var exprRef = new ReferenceExpressionRef_Accessor("redis://localhost:6379", null);
        var handles = new HandleRegistry();

        // Act
        var expr = exprRef.ToReferenceExpression(handles, "test/cap", "param");

        // Assert
        Assert.NotNull(expr);
        Assert.Equal("redis://localhost:6379", expr.ValueExpression);
    }

    [Fact]
    public async Task ToReferenceExpression_WithValueProviders_CreatesExpression()
    {
        // Arrange
        var handles = new HandleRegistry();
        var endpoint1 = new TestValueProvider("localhost", "{host}");
        var endpoint2 = new TestValueProvider("6379", "{port}");
        var handle1 = handles.Register(endpoint1, "aspire/EndpointReference");
        var handle2 = handles.Register(endpoint2, "aspire/EndpointReference");

        var valueProviders = new JsonNode?[]
        {
            new JsonObject { ["$handle"] = handle1 },
            new JsonObject { ["$handle"] = handle2 }
        };
        var exprRef = new ReferenceExpressionRef_Accessor("redis://{0}:{1}", valueProviders);

        // Act
        var expr = exprRef.ToReferenceExpression(handles, "test/cap", "param");

        // Assert
        Assert.NotNull(expr);
        Assert.Equal("redis://{host}:{port}", expr.ValueExpression);
        Assert.Equal("redis://localhost:6379", await expr.GetValueAsync(default));
    }

    [Fact]
    public void ToReferenceExpression_WithMissingHandle_ThrowsCapabilityException()
    {
        // Arrange
        var handles = new HandleRegistry();
        var valueProviders = new JsonNode?[]
        {
            new JsonObject { ["$handle"] = "aspire/NonExistent:999" }
        };
        var exprRef = new ReferenceExpressionRef_Accessor("redis://{0}", valueProviders);

        // Act & Assert
        var ex = Assert.Throws<CapabilityException>(() =>
            exprRef.ToReferenceExpression(handles, "test/cap", "param"));
        Assert.Equal(AtsErrorCodes.HandleNotFound, ex.Error.Code);
    }

    [Fact]
    public void ToReferenceExpression_WithStringLiteral_CreatesExpression()
    {
        // Arrange
        var handles = new HandleRegistry();
        var valueProviders = new JsonNode?[]
        {
            JsonValue.Create("6379")
        };
        var exprRef = new ReferenceExpressionRef_Accessor("redis://localhost:{0}", valueProviders);

        // Act
        var expr = exprRef.ToReferenceExpression(handles, "test/cap", "param");

        // Assert
        Assert.NotNull(expr);
        Assert.Equal("redis://localhost:6379", expr.ValueExpression);
    }

    [Fact]
    public async Task ToReferenceExpression_WithMixedValueProviders_CreatesExpression()
    {
        // Arrange
        var handles = new HandleRegistry();
        var endpoint = new TestValueProvider("myhost", "{host}");
        var handle = handles.Register(endpoint, "aspire/EndpointReference");

        var valueProviders = new JsonNode?[]
        {
            new JsonObject { ["$handle"] = handle },
            JsonValue.Create("6379")
        };
        var exprRef = new ReferenceExpressionRef_Accessor("redis://{0}:{1}", valueProviders);

        // Act
        var expr = exprRef.ToReferenceExpression(handles, "test/cap", "param");

        // Assert
        Assert.NotNull(expr);
        Assert.Equal("redis://{host}:6379", expr.ValueExpression);
        Assert.Equal("redis://myhost:6379", await expr.GetValueAsync(default));
    }

    [Fact]
    public void ToReferenceExpression_WithInvalidValueProvider_ThrowsCapabilityException()
    {
        // Arrange
        var handles = new HandleRegistry();
        var valueProviders = new JsonNode?[]
        {
            new JsonObject { ["invalid"] = "not-a-handle" }  // Invalid format (not a handle or string)
        };
        var exprRef = new ReferenceExpressionRef_Accessor("redis://{0}", valueProviders);

        // Act & Assert
        var ex = Assert.Throws<CapabilityException>(() =>
            exprRef.ToReferenceExpression(handles, "test/cap", "param"));
        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
    }

    /// <summary>
    /// Test accessor for creating ReferenceExpressionRef instances directly.
    /// </summary>
    private sealed class ReferenceExpressionRef_Accessor(string format, JsonNode?[]? valueProviders)
    {
        public ReferenceExpression ToReferenceExpression(HandleRegistry handles, string capabilityId, string paramName)
        {
            // Use reflection or parse from JSON to create the ref
            var json = new JsonObject
            {
                ["$expr"] = new JsonObject
                {
                    ["format"] = format
                }
            };

            if (valueProviders != null && valueProviders.Length > 0)
            {
                var providersArray = new JsonArray();
                foreach (var p in valueProviders)
                {
                    providersArray.Add(p?.DeepClone());
                }
                ((JsonObject)json["$expr"]!)["valueProviders"] = providersArray;
            }

            var exprRef = ReferenceExpressionRef.FromJsonNode(json);
            return (ReferenceExpression)exprRef!.ToReferenceExpression(handles, capabilityId, paramName);
        }
    }

    /// <summary>
    /// Test value provider for unit tests.
    /// </summary>
    private sealed class TestValueProvider(string value, string expression) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => expression;

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken)
        {
            return new ValueTask<string?>(value);
        }
    }
}
