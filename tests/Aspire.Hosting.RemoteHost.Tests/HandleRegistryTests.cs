// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Aspire.Hosting.RemoteHost.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class HandleRegistryTests
{
    [Fact]
    public void Register_ReturnsNumericHandleId()
    {
        var registry = new HandleRegistry();
        var obj = new object();

        var handleId = registry.Register(obj, "aspire/TestType");

        // Handle ID is now just a numeric instance ID
        Assert.True(long.TryParse(handleId, out var instanceId));
        Assert.True(instanceId > 0);
        // Type ID is retrieved separately
        Assert.Equal("aspire/TestType", registry.GetTypeId(handleId));
    }

    [Fact]
    public void Register_IncrementsInstanceIdForEachRegistration()
    {
        var registry = new HandleRegistry();

        var handle1 = registry.Register(new object(), "aspire/Test");
        var handle2 = registry.Register(new object(), "aspire/Test");
        var handle3 = registry.Register(new object(), "aspire/Test");

        // Parse as simple integers
        var id1 = long.Parse(handle1);
        var id2 = long.Parse(handle2);
        var id3 = long.Parse(handle3);

        Assert.Equal(id1 + 1, id2);
        Assert.Equal(id2 + 1, id3);
    }

    [Fact]
    public void TryGet_ReturnsTrueAndObjectForRegisteredHandle()
    {
        var registry = new HandleRegistry();
        var obj = new TestObject { Value = 42 };
        var handleId = registry.Register(obj, "aspire/TestObject");

        var found = registry.TryGet(handleId, out var retrieved, out var typeId);

        Assert.True(found);
        Assert.Same(obj, retrieved);
        Assert.Equal("aspire/TestObject", typeId);
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnregisteredHandle()
    {
        var registry = new HandleRegistry();

        var found = registry.TryGet("999", out var obj, out var typeId);

        Assert.False(found);
        Assert.Null(obj);
        Assert.Null(typeId);
    }

    [Fact]
    public void GetObject_ReturnsObjectForRegisteredHandle()
    {
        var registry = new HandleRegistry();
        var obj = new TestObject { Value = 42 };
        var handleId = registry.Register(obj, "aspire/TestObject");

        var retrieved = registry.GetObject(handleId);

        Assert.Same(obj, retrieved);
    }

    [Fact]
    public void GetObject_ThrowsForUnregisteredHandle()
    {
        var registry = new HandleRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetObject("999"));
    }

    [Fact]
    public void GetObjectGeneric_ReturnsTypedObjectForRegisteredHandle()
    {
        var registry = new HandleRegistry();
        var obj = new TestObject { Value = 42 };
        var handleId = registry.Register(obj, "aspire/TestObject");

        var retrieved = registry.GetObject<TestObject>(handleId);

        Assert.Same(obj, retrieved);
        Assert.Equal(42, retrieved.Value);
    }

    [Fact]
    public void GetObjectGeneric_ThrowsForWrongType()
    {
        var registry = new HandleRegistry();
        var obj = new TestObject { Value = 42 };
        var handleId = registry.Register(obj, "aspire/TestObject");

        Assert.Throws<InvalidOperationException>(() => registry.GetObject<string>(handleId));
    }

    [Fact]
    public void GetTypeId_ReturnsTypeIdForRegisteredHandle()
    {
        var registry = new HandleRegistry();
        var handleId = registry.Register(new object(), "aspire.redis/RedisBuilder");

        var typeId = registry.GetTypeId(handleId);

        Assert.Equal("aspire.redis/RedisBuilder", typeId);
    }

    [Fact]
    public void Contains_ReturnsTrueForRegisteredHandle()
    {
        var registry = new HandleRegistry();
        var handleId = registry.Register(new object(), "aspire/Test");

        Assert.True(registry.Contains(handleId));
    }

    [Fact]
    public void Contains_ReturnsFalseForUnregisteredHandle()
    {
        var registry = new HandleRegistry();

        Assert.False(registry.Contains("999"));
    }

    [Fact]
    public void Unregister_RemovesHandle()
    {
        var registry = new HandleRegistry();
        var handleId = registry.Register(new object(), "aspire/Test");

        var removed = registry.Unregister(handleId);

        Assert.True(removed);
        Assert.False(registry.Contains(handleId));
    }

    [Fact]
    public void Unregister_ReturnsFalseForUnregisteredHandle()
    {
        var registry = new HandleRegistry();

        var removed = registry.Unregister("999");

        Assert.False(removed);
    }

    [Fact]
    public void Marshal_RegistersObjectAndReturnsJsonHandle()
    {
        var registry = new HandleRegistry();
        var obj = new TestObject { Value = 42 };

        var json = registry.Marshal(obj, "aspire/TestObject");

        Assert.NotNull(json["$handle"]);
        Assert.NotNull(json["$type"]);
        Assert.Equal("aspire/TestObject", json["$type"]!.GetValue<string>());

        var handleId = json["$handle"]!.GetValue<string>();
        Assert.True(registry.Contains(handleId));
    }

    [Fact]
    public void Count_ReturnsNumberOfRegisteredHandles()
    {
        var registry = new HandleRegistry();

        Assert.Equal(0, registry.Count);

        registry.Register(new object(), "aspire/Test1");
        Assert.Equal(1, registry.Count);

        registry.Register(new object(), "aspire/Test2");
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task DisposeAsync_DisposesDisposableObjects()
    {
        var registry = new HandleRegistry();
        var disposable = new TestDisposable();
        registry.Register(disposable, "aspire/Disposable");

        await registry.DisposeAsync();

        Assert.True(disposable.Disposed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAsyncDisposableObjects()
    {
        var registry = new HandleRegistry();
        var asyncDisposable = new TestAsyncDisposable();
        registry.Register(asyncDisposable, "aspire/AsyncDisposable");

        await registry.DisposeAsync();

        Assert.True(asyncDisposable.Disposed);
    }

    private sealed class TestObject
    {
        public int Value { get; set; }
    }

    private sealed class TestDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public class HandleRefTests
{
    [Fact]
    public void FromJsonNode_ReturnsHandleRefForValidHandle()
    {
        var json = new JsonObject { ["$handle"] = "42" };

        var result = HandleRef.FromJsonNode(json);

        Assert.NotNull(result);
        Assert.Equal("42", result.HandleId);
    }

    [Fact]
    public void FromJsonNode_ReturnsNullForNullNode()
    {
        var result = HandleRef.FromJsonNode(null);

        Assert.Null(result);
    }

    [Fact]
    public void FromJsonNode_ReturnsNullForNonObjectNode()
    {
        var json = JsonValue.Create("not an object");

        var result = HandleRef.FromJsonNode(json);

        Assert.Null(result);
    }

    [Fact]
    public void FromJsonNode_ReturnsNullForObjectWithoutHandle()
    {
        var json = new JsonObject { ["name"] = "test", ["value"] = 42 };

        var result = HandleRef.FromJsonNode(json);

        Assert.Null(result);
    }

    [Fact]
    public void FromJsonNode_ThrowsForNonStringHandle()
    {
        var json = new JsonObject { ["$handle"] = 42 };

        // The implementation throws when trying to get a non-string as string
        Assert.Throws<InvalidOperationException>(() => HandleRef.FromJsonNode(json));
    }

    [Fact]
    public void FromJsonNode_WorksWithAdditionalProperties()
    {
        var json = new JsonObject
        {
            ["$handle"] = "1",
            ["$type"] = "aspire/Test",
            ["extra"] = "ignored"
        };

        var result = HandleRef.FromJsonNode(json);

        Assert.NotNull(result);
        Assert.Equal("1", result.HandleId);
    }

    [Fact]
    public void IsHandleRef_ReturnsTrueForHandleObject()
    {
        var json = new JsonObject { ["$handle"] = "1" };

        Assert.True(HandleRef.IsHandleRef(json));
    }

    [Fact]
    public void IsHandleRef_ReturnsFalseForNull()
    {
        Assert.False(HandleRef.IsHandleRef(null));
    }

    [Fact]
    public void IsHandleRef_ReturnsFalseForNonObject()
    {
        var json = JsonValue.Create("string");

        Assert.False(HandleRef.IsHandleRef(json));
    }

    [Fact]
    public void IsHandleRef_ReturnsFalseForObjectWithoutHandle()
    {
        var json = new JsonObject { ["name"] = "test" };

        Assert.False(HandleRef.IsHandleRef(json));
    }

    [Fact]
    public void IsHandleRef_ReturnsTrueEvenWithNonStringHandle()
    {
        // IsHandleRef only checks for key presence, not value type
        var json = new JsonObject { ["$handle"] = 42 };

        Assert.True(HandleRef.IsHandleRef(json));
    }

    [Fact]
    public void Register_ConditionalReferenceExpression_CanBeRetrievedByTypeId()
    {
        var registry = new HandleRegistry();
        var condition = new TestConditionProvider(bool.TrueString);
        var conditional = ReferenceExpression.CreateConditional(
            condition, bool.TrueString, ReferenceExpression.Create($",ssl=true"), ReferenceExpression.Empty);
        var typeId = AtsConstants.ReferenceExpressionTypeId;

        var handleId = registry.Register(conditional, typeId);
        var found = registry.TryGet(handleId, out var retrieved, out var retrievedTypeId);

        Assert.True(found);
        Assert.Same(conditional, retrieved);
        Assert.Equal(typeId, retrievedTypeId);
    }

    private sealed class TestConditionProvider(string value) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "test-condition";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => new(value);

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
            => new(value);
    }
}
