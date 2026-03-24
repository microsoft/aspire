// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TypeSystem;
using Aspire.Hosting.RemoteHost.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class CallbackProxyTests
{
    [Fact]
    public void CreateProxy_ReturnsNullForNonDelegateType()
    {
        using var factory = CreateFactory();

        var result = factory.CreateProxy("callback1", typeof(string));

        Assert.Null(result);
    }

    [Fact]
    public void CreateProxy_ReturnsDelegateForAnyDelegateType()
    {
        using var factory = CreateFactory();

        // All delegate types are now accepted - no attribute required
        var result = factory.CreateProxy("callback1", typeof(Action));

        Assert.NotNull(result);
        Assert.IsAssignableFrom<Action>(result);
    }

    [Fact]
    public void CreateProxy_ReturnsDelegateForCustomType()
    {
        using var factory = CreateFactory();

        var result = factory.CreateProxy("callback1", typeof(TestCallbackNoArgs));

        Assert.NotNull(result);
        Assert.IsAssignableFrom<TestCallbackNoArgs>(result);
    }

    [Fact]
    public void CreateProxy_CachesDelegate()
    {
        using var factory = CreateFactory();

        var result1 = factory.CreateProxy("callback1", typeof(TestCallbackNoArgs));
        var result2 = factory.CreateProxy("callback1", typeof(TestCallbackNoArgs));

        Assert.Same(result1, result2);
    }

    [Fact]
    public async Task InvokedProxy_CallsCallbackInvoker()
    {
        var invoker = new TestCallbackInvoker();
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackNoArgs)factory.CreateProxy("test-callback", typeof(TestCallbackNoArgs))!;

        await proxy();

        Assert.Single(invoker.Invocations);
        Assert.Equal("test-callback", invoker.Invocations[0].CallbackId);
    }

    [Fact]
    public async Task InvokedProxy_ReturnsResultFromInvoker()
    {
        var invoker = new TestCallbackInvoker { ResultToReturn = JsonValue.Create(42) };
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackWithIntResult)factory.CreateProxy("test-callback", typeof(TestCallbackWithIntResult))!;

        var result = await proxy();

        Assert.Equal(42, result);
    }

    [Fact]
    public void CancellationTokenRegistry_IsExposed()
    {
        using var factory = CreateFactory();

        Assert.NotNull(factory.CancellationTokenRegistry);
    }

    [Fact]
    public void Dispose_DisposesResources()
    {
        var factory = CreateFactory();

        factory.Dispose();

        // Should not throw when disposed
    }

    // Tests for callbacks with parameters (bug fix verification)
    [Fact]
    public void CreateProxy_ReturnsDelegateForCallbackWithStringParameter()
    {
        using var factory = CreateFactory();

        var result = factory.CreateProxy("callback1", typeof(TestCallbackWithString));

        Assert.NotNull(result);
        Assert.IsAssignableFrom<TestCallbackWithString>(result);
    }

    [Fact]
    public async Task InvokedProxy_PassesStringArgumentAsJson()
    {
        var invoker = new TestCallbackInvoker();
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackWithString)factory.CreateProxy("test-callback", typeof(TestCallbackWithString))!;

        await proxy("hello-world");

        Assert.Single(invoker.Invocations);
        var args = invoker.Invocations[0].Args as JsonObject;
        Assert.NotNull(args);
        // Arguments are passed with positional keys (p0, p1, p2, ...)
        Assert.Equal("hello-world", args["p0"]?.GetValue<string>());
    }

    [Fact]
    public async Task InvokedProxy_PassesMultipleArgumentsAsJson()
    {
        var invoker = new TestCallbackInvoker();
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackWithMultipleParams)factory.CreateProxy("test-callback", typeof(TestCallbackWithMultipleParams))!;

        await proxy("test-name", 42);

        Assert.Single(invoker.Invocations);
        var args = invoker.Invocations[0].Args as JsonObject;
        Assert.NotNull(args);
        // Arguments are passed with positional keys (p0, p1, p2, ...)
        Assert.Equal("test-name", args["p0"]?.GetValue<string>());
        Assert.Equal(42, args["p1"]?.GetValue<int>());
    }

    [Fact]
    public async Task InvokedProxy_WithResultReturnsCorrectValue()
    {
        var invoker = new TestCallbackInvoker { ResultToReturn = JsonValue.Create("result-value") };
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackWithStringResult)factory.CreateProxy("test-callback", typeof(TestCallbackWithStringResult))!;

        var result = await proxy("input");

        Assert.Equal("result-value", result);
    }

    [Fact]
    public async Task InvokedProxy_WithCancellationToken_IncludesTokenInArgs()
    {
        var invoker = new TestCallbackInvoker();
        using var factory = CreateFactory(invoker);
        using var cts = new CancellationTokenSource();

        var proxy = (TestCallbackWithCancellation)factory.CreateProxy("test-callback", typeof(TestCallbackWithCancellation))!;

        await proxy("test", cts.Token);

        Assert.Single(invoker.Invocations);
        var args = invoker.Invocations[0].Args as JsonObject;
        Assert.NotNull(args);
        Assert.Equal("test", args["p0"]?.GetValue<string>());
        var tokenId = args["p1"]?.GetValue<string>();
        Assert.NotNull(tokenId);
        Assert.StartsWith("ct_", tokenId);
        Assert.Equal(tokenId, args["$cancellationToken"]?.GetValue<string>());
    }

    [Fact]
    public async Task InvokedProxy_WithOnlyCancellationToken_UsesPositionalTokenId()
    {
        var invoker = new TestCallbackInvoker();
        using var factory = CreateFactory(invoker);
        using var cts = new CancellationTokenSource();

        var proxy = (TestCallbackWithOnlyCancellation)factory.CreateProxy("test-callback", typeof(TestCallbackWithOnlyCancellation))!;

        await proxy(cts.Token);

        Assert.Single(invoker.Invocations);
        var args = invoker.Invocations[0].Args as JsonObject;
        Assert.NotNull(args);
        var tokenId = args["p0"]?.GetValue<string>();
        Assert.NotNull(tokenId);
        Assert.Equal(tokenId, args["$cancellationToken"]?.GetValue<string>());
    }

    // Callback error handling tests
    [Fact]
    public async Task InvokedProxy_PropagatesExceptionFromInvoker()
    {
        var invoker = new TestCallbackInvoker
        {
            ExceptionToThrow = new InvalidOperationException("Callback failed")
        };
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackNoArgs)factory.CreateProxy("test-callback", typeof(TestCallbackNoArgs))!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy());
        Assert.Equal("Callback failed", ex.Message);
    }

    [Fact]
    public async Task InvokedProxy_WithResult_PropagatesExceptionFromInvoker()
    {
        var invoker = new TestCallbackInvoker
        {
            ExceptionToThrow = new InvalidOperationException("Callback with result failed")
        };
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackWithIntResult)factory.CreateProxy("test-callback", typeof(TestCallbackWithIntResult))!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy());
        Assert.Equal("Callback with result failed", ex.Message);
    }

    [Fact]
    public async Task InvokedProxy_PropagatesOperationCanceledException()
    {
        var invoker = new TestCallbackInvoker
        {
            ExceptionToThrow = new OperationCanceledException("Operation was cancelled")
        };
        using var factory = CreateFactory(invoker);

        var proxy = (TestCallbackNoArgs)factory.CreateProxy("test-callback", typeof(TestCallbackNoArgs))!;

        await Assert.ThrowsAsync<OperationCanceledException>(() => proxy());
    }

    [Fact]
    public void InvokedSyncVoidProxy_AppliesDtoWritebackFromResult()
    {
        var dto = new TestCallbackDto { Name = "original", Count = 0 };

        // The invoker returns the modified args (simulating TypeScript returning mutated DTO)
        var invoker = new TestCallbackInvoker
        {
            ResultToReturn = new JsonObject
            {
                ["p0"] = new JsonObject { ["name"] = "modified", ["count"] = 42 }
            }
        };
        using var factory = CreateFactory(invoker, registerDtoTypes: true);

        var proxy = (TestSyncVoidCallbackWithDto)factory.CreateProxy("test-callback", typeof(TestSyncVoidCallbackWithDto))!;

        proxy(dto);

        Assert.Equal("modified", dto.Name);
        Assert.Equal(42, dto.Count);
    }

    [Fact]
    public async Task InvokedAsyncVoidProxy_AppliesDtoWritebackFromResult()
    {
        var dto = new TestCallbackDto { Name = "original", Count = 0 };

        var invoker = new TestCallbackInvoker
        {
            ResultToReturn = new JsonObject
            {
                ["p0"] = new JsonObject { ["name"] = "async-modified", ["count"] = 99 }
            }
        };
        using var factory = CreateFactory(invoker, registerDtoTypes: true);

        var proxy = (TestAsyncVoidCallbackWithDto)factory.CreateProxy("test-callback", typeof(TestAsyncVoidCallbackWithDto))!;

        await proxy(dto);

        Assert.Equal("async-modified", dto.Name);
        Assert.Equal(99, dto.Count);
    }

    [Fact]
    public void InvokedSyncVoidProxy_DtoWritebackIgnoresNonDtoArgs()
    {
        // Use a delegate with both a non-DTO param (string) and a DTO param to exercise
        // the writeback path. The non-DTO arg at p0 should be safely skipped, while
        // the DTO arg at p1 should be written back.
        var dto = new TestCallbackDto { Name = "original", Count = 0 };
        var invoker = new TestCallbackInvoker
        {
            ResultToReturn = new JsonObject
            {
                ["p0"] = JsonValue.Create("some-string"),
                ["p1"] = new JsonObject { ["name"] = "mixed-modified", ["count"] = 77 }
            }
        };
        using var factory = CreateFactory(invoker, registerDtoTypes: true);

        var proxy = (TestSyncVoidCallbackWithMixedArgs)factory.CreateProxy("test-callback", typeof(TestSyncVoidCallbackWithMixedArgs))!;

        // Should not throw - non-DTO arg at p0 is skipped, DTO arg at p1 is written back
        proxy("hello", dto);

        Assert.Equal("mixed-modified", dto.Name);
        Assert.Equal(77, dto.Count);
    }

    [Fact]
    public void InvokedSyncVoidProxy_DtoWritebackHandlesNullResult()
    {
        var dto = new TestCallbackDto { Name = "original", Count = 0 };

        // Invoker returns null (TypeScript callback returned undefined and no args were sent back)
        var invoker = new TestCallbackInvoker { ResultToReturn = null };
        using var factory = CreateFactory(invoker, registerDtoTypes: true);

        var proxy = (TestSyncVoidCallbackWithDto)factory.CreateProxy("test-callback", typeof(TestSyncVoidCallbackWithDto))!;

        proxy(dto);

        // Original values should be unchanged
        Assert.Equal("original", dto.Name);
        Assert.Equal(0, dto.Count);
    }

    [Fact]
    public void InvokedSyncVoidProxy_AppliesWritebackToMultipleDtos()
    {
        var dto1 = new TestCallbackDto { Name = "first", Count = 1 };
        var dto2 = new TestCallbackDto { Name = "second", Count = 2 };

        var invoker = new TestCallbackInvoker
        {
            ResultToReturn = new JsonObject
            {
                ["p0"] = new JsonObject { ["name"] = "first-updated", ["count"] = 10 },
                ["p1"] = new JsonObject { ["name"] = "second-updated", ["count"] = 20 }
            }
        };
        using var factory = CreateFactory(invoker, registerDtoTypes: true);

        var proxy = (TestSyncVoidCallbackWithMultipleDtos)factory.CreateProxy("test-callback", typeof(TestSyncVoidCallbackWithMultipleDtos))!;

        proxy(dto1, dto2);

        Assert.Equal("first-updated", dto1.Name);
        Assert.Equal(10, dto1.Count);
        Assert.Equal("second-updated", dto2.Name);
        Assert.Equal(20, dto2.Count);
    }

    private static AtsCallbackProxyFactory CreateFactory(ICallbackInvoker? invoker = null, bool registerDtoTypes = false)
    {
        var handles = new HandleRegistry();
        var ctRegistry = new CancellationTokenRegistry();
        var dtoTypes = registerDtoTypes
            ? new List<AtsDtoTypeInfo>
            {
                new() { TypeId = "test/TestCallbackDto", Name = "TestCallbackDto", ClrType = typeof(TestCallbackDto), Properties = [] }
            }
            : [];
        var context = new AtsContext { Capabilities = [], HandleTypes = [], DtoTypes = dtoTypes, EnumTypes = [] };
        var marshaller = new AtsMarshaller(handles, context, ctRegistry, new Lazy<AtsCallbackProxyFactory>(() => throw new NotImplementedException()));
        return new AtsCallbackProxyFactory(invoker ?? new TestCallbackInvoker(), handles, ctRegistry, marshaller);
    }

    // Test delegates - any delegate type is now treated as a callback
    public delegate Task TestCallback(string value);

    public delegate Task TestCallbackNoArgs();

    public delegate Task<int> TestCallbackWithIntResult();

    public delegate Task TestCallbackWithString(string value);

    public delegate Task TestCallbackWithMultipleParams(string name, int count);

    public delegate Task<string> TestCallbackWithStringResult(string input);

    public delegate Task TestCallbackWithCancellation(string value, CancellationToken cancellationToken);

    public delegate Task TestCallbackWithOnlyCancellation(CancellationToken cancellationToken);

    public delegate void TestSyncVoidCallbackWithDto(TestCallbackDto dto);

    public delegate Task TestAsyncVoidCallbackWithDto(TestCallbackDto dto);

    public delegate void TestSyncVoidCallbackWithMixedArgs(string label, TestCallbackDto dto);

    public delegate void TestSyncVoidCallbackWithMultipleDtos(TestCallbackDto dto1, TestCallbackDto dto2);

    [AspireDto]
    public sealed class TestCallbackDto
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }
}

internal sealed class TestCallbackInvoker : ICallbackInvoker
{
    public List<(string CallbackId, JsonNode? Args)> Invocations { get; } = [];
    public JsonNode? ResultToReturn { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public bool IsConnected => true;

    public Task<TResult> InvokeAsync<TResult>(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
    {
        Invocations.Add((callbackId, args));
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(ResultToReturn is TResult result ? result : default!);
    }

    public Task InvokeAsync(string callbackId, JsonNode? args, CancellationToken cancellationToken = default)
    {
        Invocations.Add((callbackId, args));
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.CompletedTask;
    }
}
