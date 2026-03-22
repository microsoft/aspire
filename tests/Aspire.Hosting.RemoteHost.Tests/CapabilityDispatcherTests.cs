// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.TypeSystem;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class CapabilityDispatcherTests
{
    [Fact]
    public void Register_AddsCapabilityThatCanBeInvoked()
    {
        var dispatcher = CreateDispatcher();
        CapabilityHandler handler = (args, handles) => Task.FromResult<JsonNode?>(JsonValue.Create("result"));

        dispatcher.Register("test/capability@1", handler);

        Assert.True(dispatcher.HasCapability("test/capability@1"));
    }

    [Fact]
    public void HasCapability_ReturnsFalseForUnregisteredCapability()
    {
        var dispatcher = CreateDispatcher();

        Assert.False(dispatcher.HasCapability("nonexistent/capability@1"));
    }

    [Fact]
    public void GetCapabilityIds_ReturnsAllRegisteredCapabilities()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Register("test/cap1@1", (_, _) => Task.FromResult<JsonNode?>(null));
        dispatcher.Register("test/cap2@1", (_, _) => Task.FromResult<JsonNode?>(null));

        var ids = dispatcher.GetCapabilityIds().ToList();

        Assert.Contains("test/cap1@1", ids);
        Assert.Contains("test/cap2@1", ids);
    }

    [Fact]
    public void Invoke_CallsRegisteredHandler()
    {
        var dispatcher = CreateDispatcher();
        var called = false;
        dispatcher.Register("test/capability@1", (args, handles) =>
        {
            called = true;
            return Task.FromResult<JsonNode?>(JsonValue.Create("success"));
        });

        dispatcher.Invoke("test/capability@1", null);

        Assert.True(called);
    }

    [Fact]
    public void Invoke_PassesArgumentsToHandler()
    {
        var dispatcher = CreateDispatcher();
        string? receivedName = null;
        dispatcher.Register("test/capability@1", (args, handles) =>
        {
            receivedName = args?["name"]?.GetValue<string>();
            return Task.FromResult<JsonNode?>(null);
        });

        dispatcher.Invoke("test/capability@1", new JsonObject { ["name"] = "test-value" });

        Assert.Equal("test-value", receivedName);
    }

    [Fact]
    public void Invoke_ReturnsHandlerResult()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Register("test/capability@1", (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create(42)));

        var result = dispatcher.Invoke("test/capability@1", null);

        Assert.NotNull(result);
        Assert.Equal(42, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_ThrowsCapabilityNotFoundForUnregisteredCapability()
    {
        var dispatcher = CreateDispatcher();

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("nonexistent/capability@1", null));

        Assert.Equal(AtsErrorCodes.CapabilityNotFound, ex.Error.Code);
    }

    [Fact]
    public void Invoke_WrapsHandlerExceptionsAsInternalError()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Register("test/capability@1", (_, _) =>
        {
            throw new InvalidOperationException("Handler failed");
#pragma warning disable CS0162 // Unreachable code detected - needed for return type inference
            return Task.FromResult<JsonNode?>(null);
#pragma warning restore CS0162
        });

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("test/capability@1", null));

        Assert.Equal(AtsErrorCodes.InternalError, ex.Error.Code);
        Assert.Contains("Handler failed", ex.Message);
    }

    [Fact]
    public void Invoke_PropagatesCapabilityExceptionsDirectly()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Register("test/capability@1", (_, _) =>
        {
            throw CapabilityException.InvalidArgument("test/capability@1", "param", "Bad value");
#pragma warning disable CS0162 // Unreachable code detected - needed for return type inference
            return Task.FromResult<JsonNode?>(null);
#pragma warning restore CS0162
        });

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("test/capability@1", null));

        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
    }

    [Fact]
    public void Invoke_ConvertsInvalidCastToTypeMismatch()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Register("test/capability@1", (_, _) =>
        {
            throw new InvalidCastException("Cannot cast to expected type");
#pragma warning disable CS0162 // Unreachable code detected - needed for return type inference
            return Task.FromResult<JsonNode?>(null);
#pragma warning restore CS0162
        });

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("test/capability@1", null));

        Assert.Equal(AtsErrorCodes.TypeMismatch, ex.Error.Code);
    }

    [Fact]
    public void Constructor_ScansAssemblyForAspireExportAttributes()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);

        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/testMethod"));
    }

    [Fact]
    public void Invoke_CanCallScannedCapability()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "hello" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/testMethod", args);

        Assert.NotNull(result);
        Assert.Equal("HELLO", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_HandlesOptionalParameters()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["required"] = "test" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/withOptional", args);

        Assert.NotNull(result);
        Assert.Equal("test:default", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_UsesProvidedOptionalParameter()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["required"] = "test", ["optional"] = "custom" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/withOptional", args);

        Assert.NotNull(result);
        Assert.Equal("test:custom", result.GetValue<string>());
    }

    // Context type tests
    [Fact]
    public void Constructor_RegistersContextTypeProperties()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);

        // Properties should be registered with getter capabilities using derived type IDs
        // Type ID = {AssemblyName}/{FullTypeName}
        // Capability ID = {Package}/{TypeName}.{propertyName} (camelCase, no "get" prefix)
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.name"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.count"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.isEnabled"));
    }

    [Fact]
    public void Constructor_SkipsNonAtsCompatibleProperties()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);

        // IDisposable is not ATS-compatible, so this property should be skipped
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.nonAtsProperty"));
    }

    [Fact]
    public void Invoke_ContextTypePropertyReturnsValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestContextType).Assembly]);

        // Create and register a context object
        // Type ID = {AssemblyName}/{FullTypeName}
        var context = new TestContextType { Name = "test-name", Count = 100 };
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        var nameResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.name", args);
        var countResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.count", args);

        Assert.NotNull(nameResult);
        Assert.Equal("test-name", nameResult.GetValue<string>());
        Assert.NotNull(countResult);
        Assert.Equal(100, countResult.GetValue<int>());
    }

    [Fact]
    public void Invoke_ContextTypePropertyThrowsWhenContextMissing()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.name", null));

        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void Invoke_ContextTypePropertyThrowsWhenContextNotHandle()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);
        var args = new JsonObject { ["context"] = "not-a-handle" };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.name", args));

        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
    }

    [Fact]
    public void Invoke_ContextTypePropertyThrowsWhenHandleNotFound()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType:999" } };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.name", args));

        Assert.Equal(AtsErrorCodes.HandleNotFound, ex.Error.Code);
    }

    [Fact]
    public void Invoke_CancellationTokenPropertyResultCanBePassedToCapability()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestCancellationTokenContext).Assembly]);
        using var cts = new CancellationTokenSource();

        var context = new TestCancellationTokenContext { CancellationToken = cts.Token };
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestCancellationTokenContext");
        var getArgs = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        var tokenResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestCancellationTokenContext.cancellationToken", getArgs);

        Assert.NotNull(tokenResult);
        cts.Cancel();

        var invokeArgs = new JsonObject { ["cancellationToken"] = tokenResult };
        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/canObserveCancellation", invokeArgs);

        Assert.NotNull(result);
        Assert.True(result.GetValue<bool>());
    }

    [Fact]
    public void Constructor_RegistersVersionedContextTypeProperties()
    {
        var dispatcher = CreateDispatcher(typeof(VersionedContextType).Assembly);

        // Versioned context type properties should also be registered with derived type ID
        // Capability ID = {Package}/{TypeName}.{propertyName} (camelCase, no "get" prefix)
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/VersionedContextType.value"));
    }

    /// <summary>
    /// Tests that context type properties whose type is another context type are registered.
    /// This simulates the EnvironmentCallbackContext.ExecutionContext scenario.
    /// </summary>
    [Fact]
    public void Constructor_RegistersNestedContextTypeProperties()
    {
        var dispatcher = CreateDispatcher(typeof(TestParentContextType).Assembly);

        // The TestParentContextType has a NestedContext property of type TestNestedContextType
        // This capability should be registered because TestNestedContextType is a valid ATS type
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestParentContextType.nestedContext"),
            "NestedContext property capability should be registered");
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestParentContextType.name"),
            "Name property capability should be registered");

        // The nested type's properties should also be registered
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestNestedContextType.operation"),
            "Nested context type's Operation property should be registered");
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestNestedContextType.isPublishMode"),
            "Nested context type's IsPublishMode property should be registered");
    }

    /// <summary>
    /// Tests that getting a context type property that returns another context type works.
    /// </summary>
    [Fact]
    public void Invoke_NestedContextTypePropertyReturnsHandle()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestParentContextType).Assembly]);

        // Create and register the parent context with a nested context
        var nestedContext = new TestNestedContextType { Operation = "Publish", IsPublishMode = true };
        var parentContext = new TestParentContextType(nestedContext) { Name = "parent-context" };
        var handleId = handles.Register(parentContext, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestParentContextType");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        // Get the nested context property - should return a handle
        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestParentContextType.nestedContext", args);

        Assert.NotNull(result);
        // The result should be a handle reference to the nested context
        var handleRef = result.AsObject();
        Assert.NotNull(handleRef);
        Assert.True(handleRef.ContainsKey("$handle"), "Result should be a handle reference");
        Assert.True(handleRef.ContainsKey("$type"), "Result should have a type");
        var nestedHandleId = handleRef["$handle"]!.GetValue<string>();
        var nestedTypeId = handleRef["$type"]!.GetValue<string>();
        // Handle ID is now just a numeric instance ID
        Assert.True(long.TryParse(nestedHandleId, out _), "Handle ID should be numeric");
        // Type ID is in the $type field
        Assert.StartsWith("Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestNestedContextType", nestedTypeId);

        // Verify we can use the returned handle to access the nested context's properties
        var nestedArgs = new JsonObject { ["context"] = new JsonObject { ["$handle"] = nestedHandleId } };
        var operationResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestNestedContextType.operation", nestedArgs);
        Assert.NotNull(operationResult);
        Assert.Equal("Publish", operationResult.GetValue<string>());
    }

    /// <summary>
    /// Tests that EnvironmentCallbackContext.executionContext capability is registered.
    /// This is the exact scenario that was failing at runtime.
    /// </summary>
    [Fact]
    public void Constructor_RegistersEnvironmentCallbackContextExecutionContextCapability()
    {
        // Scan the Aspire.Hosting assembly which contains both:
        // - EnvironmentCallbackContext (has ExecutionContext property)
        // - DistributedApplicationExecutionContext (the property type)
        var dispatcher = CreateDispatcher(typeof(AspireExportAttribute).Assembly);

        // EnvironmentCallbackContext.ExecutionContext should be registered
        // Capability ID format: {Namespace}/{TypeName}.{camelCasePropertyName}
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext"),
            "EnvironmentCallbackContext.executionContext capability should be registered");

        // Other EnvironmentCallbackContext properties should also be registered
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environmentVariables"),
            "EnvironmentCallbackContext.environmentVariables should be registered");

        // DistributedApplicationExecutionContext properties should also be registered
        Assert.True(dispatcher.HasCapability("Aspire.Hosting/DistributedApplicationExecutionContext.operation"),
            "DistributedApplicationExecutionContext.operation should be registered");
        Assert.True(dispatcher.HasCapability("Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode"),
            "DistributedApplicationExecutionContext.isPublishMode should be registered");
        Assert.True(dispatcher.HasCapability("Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode"),
            "DistributedApplicationExecutionContext.isRunMode should be registered");
    }

    // Property setter tests
    [Fact]
    public void Constructor_RegistersPropertySetters()
    {
        var dispatcher = CreateDispatcher(typeof(TestContextType).Assembly);

        // Property setters should be registered with "set" prefix and PascalCase property name
        // Capability ID = {Package}/{TypeName}.set{PropertyName}
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.setName"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.setCount"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestContextType.setIsEnabled"));
    }

    [Fact]
    public void Invoke_PropertySetterUpdatesValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestContextType).Assembly]);

        // Create and register a context object
        var context = new TestContextType { Name = "original-name" };
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["value"] = "new-name"
        };

        // Invoke the setter
        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.setName", args);

        // Verify the value was updated
        Assert.Equal("new-name", context.Name);
    }

    [Fact]
    public void Invoke_PropertySetterUpdatesIntValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestContextType).Assembly]);

        var context = new TestContextType { Count = 10 };
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["value"] = 99
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.setCount", args);

        Assert.Equal(99, context.Count);
    }

    [Fact]
    public void Invoke_PropertySetterUpdatesBoolValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestContextType).Assembly]);

        var context = new TestContextType { IsEnabled = true };
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["value"] = false
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.setIsEnabled", args);

        Assert.False(context.IsEnabled);
    }

    [Fact]
    public void Invoke_PropertySetterThrowsWhenValueMissing()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestContextType).Assembly]);

        var context = new TestContextType();
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestContextType");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId }
            // Missing "value" parameter
        };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestContextType.setName", args));

        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
        Assert.Contains("value", ex.Message);
    }

    // Instance method tests
    [Fact]
    public void Constructor_RegistersInstanceMethods()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeWithMethods).Assembly);

        // Instance methods should be registered with capability ID format: {Package}/{TypeName}.{methodName}
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.doSomething"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.calculateSum"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.processAsync"));
    }

    [Fact]
    public void Invoke_InstanceMethodCallsMethodOnContext()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeWithMethods).Assembly]);

        var context = new TestTypeWithMethods();
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestTypeWithMethods");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId }
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.doSomething", args);

        Assert.True(context.WasCalled);
    }

    [Fact]
    public void Invoke_InstanceMethodWithParameters()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeWithMethods).Assembly]);

        var context = new TestTypeWithMethods();
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestTypeWithMethods");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["a"] = 5,
            ["b"] = 7
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.calculateSum", args);

        Assert.NotNull(result);
        Assert.Equal(12, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_AsyncInstanceMethod()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeWithMethods).Assembly]);

        var context = new TestTypeWithMethods();
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestTypeWithMethods");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["input"] = "test"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.processAsync", args);

        Assert.NotNull(result);
        Assert.Equal("TEST", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_ValueTaskInstanceMethod()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeWithMethods).Assembly]);

        var context = new TestTypeWithMethods();
        var handleId = handles.Register(context, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestTypeWithMethods");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["input"] = "value-task"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.processValueTaskAsync", args);

        Assert.NotNull(result);
        Assert.Equal("VALUE-TASK", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_StaticValueTaskMethod()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "value-task" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/asyncValueTaskWithResult", args);

        Assert.NotNull(result);
        Assert.Equal("VALUE-TASK", result.GetValue<string>());
    }

    [Fact]
    public void Constructor_SkipsNonPublicMethods()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeWithMethods).Assembly);

        // Private methods should not be exposed
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.privateMethod"));
    }

    [Fact]
    public void Constructor_SkipsSpecialMethods()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeWithMethods).Assembly);

        // Property getters/setters via ExposeMethods should not create duplicate capabilities
        // (they are handled by ExposeProperties separately)
        // Object methods like ToString, GetHashCode should be skipped
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.toString"));
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.getHashCode"));
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.equals"));
        Assert.False(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/TestTypeWithMethods.getType"));
    }

    // Callback parameter tests
    [Fact]
    public void Invoke_MethodWithCallbackParameter()
    {
        var handles = new HandleRegistry();
        var invoker = new TestCallbackInvoker();
        var (marshaller, callbackFactory) = CreateTestMarshallerWithCallbacks(handles, invoker);
        using var _ = callbackFactory;
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilitiesWithCallback).Assembly]);

        var args = new JsonObject
        {
            ["value"] = "test-input",
            ["callback"] = "my-callback-id"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/withCallback", args);

        // The method should have been called with the callback proxy
        // The callback proxy invokes our TestCallbackInvoker
        Assert.NotNull(result);
        Assert.Equal("PROCESSED: test-input", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_MethodWithCallbackParameter_InvokesCallback()
    {
        var handles = new HandleRegistry();
        var invoker = new TestCallbackInvoker();
        var (marshaller, callbackFactory) = CreateTestMarshallerWithCallbacks(handles, invoker);
        using var _ = callbackFactory;
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilitiesWithCallback).Assembly]);

        var args = new JsonObject
        {
            ["callback"] = "progress-callback"
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/invokeCallback", args);

        // Verify the callback was invoked
        Assert.Single(invoker.Invocations);
        Assert.Equal("progress-callback", invoker.Invocations[0].CallbackId);
    }

    [Fact]
    public void Invoke_MethodWithCallbackParameter_PassesArgsToCallback()
    {
        var handles = new HandleRegistry();
        var invoker = new TestCallbackInvoker();
        var (marshaller, callbackFactory) = CreateTestMarshallerWithCallbacks(handles, invoker);
        using var _ = callbackFactory;
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilitiesWithCallback).Assembly]);

        var args = new JsonObject
        {
            ["callback"] = "typed-callback"
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/invokeTypedCallback", args);

        Assert.Single(invoker.Invocations);
        var callbackArgs = invoker.Invocations[0].Args as JsonObject;
        Assert.NotNull(callbackArgs);
        // Callback arguments are passed with positional keys (p0, p1, p2, ...)
        Assert.Equal("hello from C#", callbackArgs["p0"]?.GetValue<string>());
    }

    [Fact]
    public void Invoke_MethodWithAsyncCallback()
    {
        var handles = new HandleRegistry();
        var invoker = new TestCallbackInvoker { ResultToReturn = JsonValue.Create(42) };
        var (marshaller, callbackFactory) = CreateTestMarshallerWithCallbacks(handles, invoker);
        using var _ = callbackFactory;
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilitiesWithCallback).Assembly]);

        var args = new JsonObject
        {
            ["callback"] = "async-callback"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/withAsyncCallback", args);

        Assert.NotNull(result);
        Assert.Equal(42, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_SyncCapabilityWithoutBackgroundThreadOptIn_RunsInline()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilitiesWithBackgroundThreadDispatch).Assembly);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/syncInlineThreadProbe", null);

        Assert.NotNull(result);
        Assert.Equal(callerThreadId, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_SyncCapabilityWithBackgroundThreadOptIn_RunsOnBackgroundThread()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilitiesWithBackgroundThreadDispatch).Assembly);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/syncBackgroundThreadProbe", null);

        Assert.NotNull(result);
        Assert.NotEqual(callerThreadId, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_ValueTaskCapabilityWithBackgroundThreadOptIn_RunsInline()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilitiesWithBackgroundThreadDispatch).Assembly);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/valueTaskBackgroundThreadProbe", null);

        Assert.NotNull(result);
        Assert.Equal(callerThreadId, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_NonGenericValueTaskCapabilityWithBackgroundThreadOptIn_RunsInline()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilitiesWithBackgroundThreadDispatch).Assembly);
        var callerThreadId = Environment.CurrentManagedThreadId;

        TestCapabilitiesWithBackgroundThreadDispatch.ResetLastObservedThreadId();

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/nonGenericValueTaskBackgroundThreadProbe", null);

        Assert.Equal(callerThreadId, TestCapabilitiesWithBackgroundThreadDispatch.LastObservedThreadId);
    }

    [Fact]
    public void Invoke_MethodWithoutCallbackFactory_ThrowsForCallbackParameter()
    {
        var handles = new HandleRegistry();
        // No callback factory provided - use marshaller that throws on callback access
        var marshaller = CreateTestMarshaller(handles);
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(TestCapabilitiesWithCallback).Assembly]);

        var args = new JsonObject
        {
            ["value"] = "test",
            ["callback"] = "some-callback"
        };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/withCallback", args));

        Assert.Equal(AtsErrorCodes.InvalidArgument, ex.Error.Code);
        Assert.Contains("callback", ex.Message.ToLower());
    }

    // Async capability handler tests
    [Fact]
    public void Constructor_ScansAsyncMethods()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);

        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/asyncVoid"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/asyncWithResult"));
        Assert.True(dispatcher.HasCapability("Aspire.Hosting.RemoteHost.Tests/asyncThrows"));
    }

    [Fact]
    public void Invoke_HandlesAsyncVoidMethod()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "test" };

        // Should not throw - async void (Task) methods complete successfully
        // Note: may return the awaited Task's internal result or null
        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/asyncVoid", args);
    }

    [Fact]
    public void Invoke_HandlesAsyncMethodWithResult()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "hello" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/asyncWithResult", args);

        Assert.NotNull(result);
        Assert.Equal("HELLO", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_HandlesAsyncMethodThatThrows()
    {
        var dispatcher = CreateDispatcher(typeof(TestCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "test" };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/asyncThrows", args));

        Assert.Equal(AtsErrorCodes.InternalError, ex.Error.Code);
        Assert.Contains("Async error", ex.Message);
    }

    // Array Marshalling tests
    [Fact]
    public void Invoke_AcceptsArrayParameter()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeCategoryCapabilities).Assembly);
        var args = new JsonObject
        {
            ["values"] = new JsonArray { 1, 2, 3, 4, 5 }
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/sumArray", args);

        Assert.NotNull(result);
        Assert.Equal(15, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_ReturnsArrayResult()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeCategoryCapabilities).Assembly);
        var args = new JsonObject { ["count"] = 3 };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnArray", args);

        Assert.NotNull(result);
        var resultArray = result as JsonArray;
        Assert.NotNull(resultArray);
        Assert.Equal(3, resultArray.Count);
        Assert.Equal("item0", resultArray[0]?.GetValue<string>());
        Assert.Equal("item1", resultArray[1]?.GetValue<string>());
        Assert.Equal("item2", resultArray[2]?.GetValue<string>());
    }

    [Fact]
    public void Invoke_AcceptsReadOnlyListParameter()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeCategoryCapabilities).Assembly);
        var args = new JsonObject
        {
            ["values"] = new JsonArray { 10, 20, 30 }
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptReadOnlyList", args);

        Assert.NotNull(result);
        Assert.Equal(60, result.GetValue<int>());
    }

    // Union type tests
    [Fact]
    public void Invoke_AcceptsUnionWithString()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeCategoryCapabilities).Assembly);
        var args = new JsonObject { ["value"] = "hello" };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptUnion", args);

        Assert.NotNull(result);
        Assert.Equal("hello", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_AcceptsUnionWithInt()
    {
        var dispatcher = CreateDispatcher(typeof(TestTypeCategoryCapabilities).Assembly);
        var args = new JsonObject { ["value"] = 42 };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptUnion", args);

        Assert.NotNull(result);
        Assert.Equal("42", result.GetValue<string>());
    }

    // List operations tests
    [Fact]
    public void Invoke_ReturnsMutableListAsHandle()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly]);

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableList", null);

        // Mutable lists should be returned as handles
        Assert.NotNull(result);
        var resultObj = result as JsonObject;
        Assert.NotNull(resultObj);
        Assert.True(resultObj.ContainsKey("$handle"));
    }

    [Fact]
    public void Invoke_ListGet_ReturnsItemAtIndex()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a list and get its handle
        var listResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableList", null);
        var listHandle = (listResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(listHandle);

        // Get item at index 1
        var args = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle },
            ["index"] = 1
        };

        var result = dispatcher.Invoke("Aspire.Hosting/List.get", args);

        Assert.NotNull(result);
        Assert.Equal("second", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_ListRemoveAt_RemovesItem()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a list and get its handle
        var listResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableList", null);
        var listHandle = (listResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(listHandle);

        // Remove item at index 1 ("second")
        var removeArgs = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle },
            ["index"] = 1
        };
        var removeResult = dispatcher.Invoke("Aspire.Hosting/List.removeAt", removeArgs);

        Assert.NotNull(removeResult);
        Assert.True(removeResult.GetValue<bool>());

        // Verify length is now 2
        var lengthArgs = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle }
        };
        var lengthResult = dispatcher.Invoke("Aspire.Hosting/List.length", lengthArgs);

        Assert.NotNull(lengthResult);
        Assert.Equal(2, lengthResult.GetValue<int>());
    }

    [Fact]
    public void Invoke_ListLength_ReturnsCount()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a list and get its handle
        var listResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableList", null);
        var listHandle = (listResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(listHandle);

        var args = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle }
        };

        var result = dispatcher.Invoke("Aspire.Hosting/List.length", args);

        Assert.NotNull(result);
        Assert.Equal(3, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_ListClear_RemovesAllItems()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a list and get its handle
        var listResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableList", null);
        var listHandle = (listResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(listHandle);

        // Clear the list
        var clearArgs = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle }
        };
        dispatcher.Invoke("Aspire.Hosting/List.clear", clearArgs);

        // Verify length is 0
        var lengthArgs = new JsonObject
        {
            ["list"] = new JsonObject { ["$handle"] = listHandle }
        };
        var lengthResult = dispatcher.Invoke("Aspire.Hosting/List.length", lengthArgs);

        Assert.NotNull(lengthResult);
        Assert.Equal(0, lengthResult.GetValue<int>());
    }

    // Dict operations tests
    [Fact]
    public void Invoke_ReturnsMutableDictAsHandle()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly]);

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);

        // Mutable dicts should be returned as handles
        Assert.NotNull(result);
        var resultObj = result as JsonObject;
        Assert.NotNull(resultObj);
        Assert.True(resultObj.ContainsKey("$handle"));
    }

    [Fact]
    public void Invoke_DictGet_ReturnsValueForKey()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a dict and get its handle
        var dictResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);
        var dictHandle = (dictResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(dictHandle);

        // Get value for "key1"
        var args = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle },
            ["key"] = "key1"
        };

        var result = dispatcher.Invoke("Aspire.Hosting/Dict.get", args);

        Assert.NotNull(result);
        Assert.Equal("value1", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_DictRemove_RemovesKey()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a dict and get its handle
        var dictResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);
        var dictHandle = (dictResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(dictHandle);

        // Remove "key1"
        var removeArgs = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle },
            ["key"] = "key1"
        };
        var removeResult = dispatcher.Invoke("Aspire.Hosting/Dict.remove", removeArgs);

        Assert.NotNull(removeResult);
        Assert.True(removeResult.GetValue<bool>());

        // Verify count is now 1
        var countArgs = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle }
        };
        var countResult = dispatcher.Invoke("Aspire.Hosting/Dict.count", countArgs);

        Assert.NotNull(countResult);
        Assert.Equal(1, countResult.GetValue<int>());
    }

    [Fact]
    public void Invoke_DictHas_ReturnsTrueForExistingKey()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a dict and get its handle
        var dictResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);
        var dictHandle = (dictResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(dictHandle);

        // Check for existing key
        var hasArgs = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle },
            ["key"] = "key1"
        };
        var hasResult = dispatcher.Invoke("Aspire.Hosting/Dict.has", hasArgs);

        Assert.NotNull(hasResult);
        Assert.True(hasResult.GetValue<bool>());

        // Check for non-existing key
        var hasArgs2 = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle },
            ["key"] = "nonexistent"
        };
        var hasResult2 = dispatcher.Invoke("Aspire.Hosting/Dict.has", hasArgs2);

        Assert.NotNull(hasResult2);
        Assert.False(hasResult2.GetValue<bool>());
    }

    [Fact]
    public void Invoke_DictKeys_ReturnsAllKeys()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a dict and get its handle
        var dictResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);
        var dictHandle = (dictResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(dictHandle);

        var args = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle }
        };

        var result = dispatcher.Invoke("Aspire.Hosting/Dict.keys", args);

        Assert.NotNull(result);
        var keysArray = result as JsonArray;
        Assert.NotNull(keysArray);
        Assert.Equal(2, keysArray.Count);
        Assert.Contains("key1", keysArray.Select(k => k?.GetValue<string>()));
        Assert.Contains("key2", keysArray.Select(k => k?.GetValue<string>()));
    }

    [Fact]
    public void Invoke_DictCount_ReturnsEntryCount()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestTypeCategoryCapabilities).Assembly, typeof(AspireExportAttribute).Assembly]);

        // Create a dict and get its handle
        var dictResult = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnMutableDict", null);
        var dictHandle = (dictResult as JsonObject)?["$handle"]?.GetValue<string>();
        Assert.NotNull(dictHandle);

        var args = new JsonObject
        {
            ["dict"] = new JsonObject { ["$handle"] = dictHandle }
        };

        var result = dispatcher.Invoke("Aspire.Hosting/Dict.count", args);

        Assert.NotNull(result);
        Assert.Equal(2, result.GetValue<int>());
    }

    [Fact]
    public void Invoke_AcceptEnum_DispatchesEnumFromString()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestEnumCapabilities).Assembly]);

        var args = new JsonObject
        {
            ["value"] = "ValueB" // Enum as string
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptEnum", args);

        Assert.NotNull(result);
        Assert.Equal("Received: ValueB", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_AcceptEnum_DispatchesEnumFromStringCaseInsensitive()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestEnumCapabilities).Assembly]);

        var args = new JsonObject
        {
            ["value"] = "valuec" // lowercase
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptEnum", args);

        Assert.NotNull(result);
        Assert.Equal("Received: ValueC", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_ReturnEnum_ReturnsEnumAsString()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestEnumCapabilities).Assembly]);

        var args = new JsonObject
        {
            ["name"] = "ValueA"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/returnEnum", args);

        Assert.NotNull(result);
        Assert.Equal("ValueA", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_AcceptOptionalEnum_WithValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestEnumCapabilities).Assembly]);

        var args = new JsonObject
        {
            ["value"] = "ValueB"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptOptionalEnum", args);

        Assert.NotNull(result);
        Assert.Equal("Received: ValueB", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_AcceptOptionalEnum_WithoutValue()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestEnumCapabilities).Assembly]);

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/acceptOptionalEnum", new JsonObject());

        Assert.NotNull(result);
        Assert.Equal("No value", result.GetValue<string>());
    }

    [Fact]
    public void GetDto_DeserializesEnumPropertyFromString()
    {
        var args = new JsonObject
        {
            ["dto"] = new JsonObject
            {
                ["label"] = "test-item",
                ["status"] = "ValueB"
            }
        };

        var result = args.GetDto<TestDtoWithEnum>("dto");

        Assert.NotNull(result);
        Assert.Equal("test-item", result.Label);
        Assert.Equal(TestDispatchEnum.ValueB, result.Status);
    }

    [Fact]
    public void GetDto_DeserializesEnumPropertyFromStringCaseInsensitive()
    {
        var args = new JsonObject
        {
            ["dto"] = new JsonObject
            {
                ["label"] = "item",
                ["status"] = "valuec"
            }
        };

        var result = args.GetDto<TestDtoWithEnum>("dto");

        Assert.NotNull(result);
        Assert.Equal(TestDispatchEnum.ValueC, result.Status);
    }

    [Fact]
    public void GetDto_ReturnsNullWhenPropertyMissing()
    {
        var args = new JsonObject();

        var result = args.GetDto<TestDtoWithEnum>("dto");

        Assert.Null(result);
    }

    // Builder-to-resource unwrapping tests
    // These test the scenario where a handle contains an IResourceBuilder<T> but the
    // property/method being invoked is declared on the resource type T.
    // This happens in TypeScript polyglot apps when ATS maps both IResourceBuilder<T>
    // and T to the same type ID.
    [Fact]
    public void Invoke_PropertyGetter_ResolvesBuilderToResource()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithProperties).Assembly]);

        // Create a resource and wrap it in a builder, but register the BUILDER in the handle registry
        var resource = new TestResourceWithProperties("test-resource") { Color = "blue" };
        var builder = new TestResourceBuilder<TestResourceWithProperties>(resource);
        var handleId = handles.Register(builder, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithProperties");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        // Invoke the property getter - the property is on TestResourceWithProperties, but handle contains the builder
        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithProperties.color", args);

        Assert.NotNull(result);
        Assert.Equal("blue", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_PropertySetter_ResolvesBuilderToResource()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithProperties).Assembly]);

        var resource = new TestResourceWithProperties("test-resource") { Color = "red" };
        var builder = new TestResourceBuilder<TestResourceWithProperties>(resource);
        var handleId = handles.Register(builder, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithProperties");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["value"] = "green"
        };

        dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithProperties.setColor", args);

        Assert.Equal("green", resource.Color);
    }

    [Fact]
    public void Invoke_InstanceMethod_ResolvesBuilderToResource()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithMethods).Assembly]);

        var resource = new TestResourceWithMethods("test-resource");
        var builder = new TestResourceBuilder<TestResourceWithMethods>(resource);
        var handleId = handles.Register(builder, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithMethods");
        var args = new JsonObject
        {
            ["context"] = new JsonObject { ["$handle"] = handleId },
            ["prefix"] = "hello"
        };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithMethods.greet", args);

        Assert.NotNull(result);
        Assert.Equal("hello, test-resource!", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_PropertyGetter_WorksDirectlyOnResource()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithProperties).Assembly]);

        // When the handle contains the resource directly (not wrapped in a builder), it should still work
        var resource = new TestResourceWithProperties("test-resource") { Color = "yellow" };
        var handleId = handles.Register(resource, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithProperties");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithProperties.color", args);

        Assert.NotNull(result);
        Assert.Equal("yellow", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_PropertyGetter_ResolvesInheritedPropertyViaBuilder()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithProperties).Assembly]);

        var resource = new TestResourceWithProperties("my-resource") { Color = "red" };
        var builder = new TestResourceBuilder<TestResourceWithProperties>(resource);
        var handleId = handles.Register(builder, "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithProperties");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        var result = dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithProperties.name", args);

        Assert.NotNull(result);
        Assert.Equal("my-resource", result.GetValue<string>());
    }

    [Fact]
    public void Invoke_PropertyGetter_ThrowsWhenHandleIsUnrelatedType()
    {
        var handles = new HandleRegistry();
        var dispatcher = new CapabilityDispatcher(handles, CreateTestMarshaller(handles), [typeof(TestResourceWithProperties).Assembly]);

        var handleId = handles.Register("not-a-resource", "Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.TestResourceWithProperties");
        var args = new JsonObject { ["context"] = new JsonObject { ["$handle"] = handleId } };

        var ex = Assert.Throws<CapabilityException>(() =>
            dispatcher.Invoke("Aspire.Hosting.RemoteHost.Tests/TestResourceWithProperties.color", args));

        Assert.Equal(AtsErrorCodes.InternalError, ex.Error.Code);
    }

    private static CapabilityDispatcher CreateDispatcher(params System.Reflection.Assembly[] assemblies)
    {
        var handles = new HandleRegistry();
        var marshaller = CreateTestMarshaller(handles);
        return new CapabilityDispatcher(handles, marshaller, assemblies);
    }

    private static AtsMarshaller CreateTestMarshaller(HandleRegistry? handles = null, CancellationTokenRegistry? ctRegistry = null)
    {
        handles ??= new HandleRegistry();
        ctRegistry ??= new CancellationTokenRegistry();
        var context = new AtsContext { Capabilities = [], HandleTypes = [], DtoTypes = [], EnumTypes = [] };
        return new AtsMarshaller(handles, context, ctRegistry, new Lazy<AtsCallbackProxyFactory>(() => throw new NotImplementedException()));
    }

    private static (AtsMarshaller Marshaller, AtsCallbackProxyFactory CallbackFactory) CreateTestMarshallerWithCallbacks(
        HandleRegistry handles,
        ICallbackInvoker invoker)
    {
        var ctRegistry = new CancellationTokenRegistry();
        var context = new AtsContext { Capabilities = [], HandleTypes = [], DtoTypes = [], EnumTypes = [] };
        AtsCallbackProxyFactory? callbackFactory = null;
        var marshaller = new AtsMarshaller(handles, context, ctRegistry, new Lazy<AtsCallbackProxyFactory>(() => callbackFactory!));
        callbackFactory = new AtsCallbackProxyFactory(invoker, handles, ctRegistry, marshaller);
        return (marshaller, callbackFactory);
    }
}

/// <summary>
/// Test capabilities for scanning.
/// </summary>
internal static class TestCapabilities
{
    [AspireExport("testMethod", Description = "Test method")]
    public static string TestMethod(string value)
    {
        return value.ToUpperInvariant();
    }

    [AspireExport("withOptional", Description = "Method with optional parameter")]
    public static string WithOptional(string required, string optional = "default")
    {
        return $"{required}:{optional}";
    }

    [AspireExport("asyncVoid", Description = "Async method returning Task")]
    public static async Task AsyncVoidMethod(string value)
    {
        await Task.Delay(1);
        _ = value; // Use the parameter to avoid warning
    }

    [AspireExport("asyncWithResult", Description = "Async method returning Task<T>")]
    public static async Task<string> AsyncWithResult(string value)
    {
        await Task.Delay(1);
        return value.ToUpperInvariant();
    }

    [AspireExport("asyncValueTaskWithResult", Description = "Async method returning ValueTask<T>")]
    public static async ValueTask<string> AsyncValueTaskWithResult(string value)
    {
        await Task.Delay(1);
        return value.ToUpperInvariant();
    }

    [AspireExport("asyncThrows", Description = "Async method that throws")]
    public static async Task<string> AsyncThrows(string value)
    {
        await Task.Delay(1);
        throw new InvalidOperationException("Async error: " + value);
    }

    [AspireExport("canObserveCancellation", Description = "Tests cancellation token round-tripping")]
    public static bool CanObserveCancellation(CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested;
    }
}

/// <summary>
/// Test context type for context type tests.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class TestContextType
{
    public string Name { get; set; } = "default-name";
    public int Count { get; set; } = 42;
    public bool IsEnabled { get; set; } = true;

    // This property should be skipped - IDisposable is not ATS-compatible
    public IDisposable? NonAtsProperty { get; set; }
}

[AspireExport(ExposeProperties = true)]
internal sealed class TestCancellationTokenContext
{
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Test context type to verify context properties work.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class VersionedContextType
{
    public string Value { get; set; } = "v2";
}

/// <summary>
/// Test nested context type - simulates DistributedApplicationExecutionContext.
/// This type is used as a property type in another context type.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class TestNestedContextType
{
    public string Operation { get; set; } = "Run";
    public bool IsPublishMode { get; set; }
}

/// <summary>
/// Test parent context type - simulates EnvironmentCallbackContext.
/// Has a property whose type is another context type (TestNestedContextType).
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class TestParentContextType
{
    public TestParentContextType(TestNestedContextType nestedContext)
    {
        NestedContext = nestedContext ?? throw new ArgumentNullException(nameof(nestedContext));
    }

    /// <summary>
    /// Property whose type is another context type - simulates ExecutionContext property.
    /// </summary>
    public TestNestedContextType NestedContext { get; }

    public string Name { get; set; } = "parent";
}

/// <summary>
/// Test type for instance method tests.
/// </summary>
[AspireExport(ExposeMethods = true)]
internal sealed class TestTypeWithMethods
{
    public bool WasCalled { get; private set; }

    public void DoSomething()
    {
        WasCalled = true;
    }

#pragma warning disable CA1822 // Mark members as static - testing instance methods
    public int CalculateSum(int a, int b)
    {
        return a + b;
    }

    public async Task<string> ProcessAsync(string input)
    {
        await Task.Delay(1);
        return input.ToUpperInvariant();
    }

    public async ValueTask<string> ProcessValueTaskAsync(string input)
    {
        await Task.Delay(1);
        return input.ToUpperInvariant();
    }

    // This should NOT be exposed - private method
#pragma warning disable IDE0051 // Remove unused private member - testing that private methods are not exposed
    private void PrivateMethod()
    {
    }
#pragma warning restore IDE0051
#pragma warning restore CA1822
}

/// <summary>
/// Test capabilities with callback parameters.
/// </summary>
internal static class TestCapabilitiesWithCallback
{
    /// <summary>
    /// A method that accepts a callback but doesn't invoke it.
    /// </summary>
    [AspireExport("withCallback", Description = "Method with callback parameter")]
    public static string WithCallback(string value, Action callback)
    {
        // The callback is provided but we don't invoke it in this test
        _ = callback;
        return $"PROCESSED: {value}";
    }

    /// <summary>
    /// A method that invokes the callback.
    /// </summary>
    [AspireExport("invokeCallback", Description = "Method that invokes callback")]
    public static void InvokeCallback(Func<Task> callback)
    {
        callback().GetAwaiter().GetResult();
    }

    /// <summary>
    /// A method that invokes a typed callback with arguments.
    /// </summary>
    [AspireExport("invokeTypedCallback", Description = "Method that invokes typed callback")]
    public static void InvokeTypedCallback(Func<string, Task> callback)
    {
        callback("hello from C#").GetAwaiter().GetResult();
    }

    /// <summary>
    /// A method with an async callback that returns a value.
    /// </summary>
    [AspireExport("withAsyncCallback", Description = "Method with async callback returning value")]
    public static int WithAsyncCallback(Func<Task<int>> callback)
    {
        return callback().GetAwaiter().GetResult();
    }
}

internal static class TestCapabilitiesWithBackgroundThreadDispatch
{
    public static int LastObservedThreadId { get; private set; }

    public static void ResetLastObservedThreadId()
    {
        LastObservedThreadId = 0;
    }

    [AspireExport("syncInlineThreadProbe", Description = "Captures the current thread for inline sync invocation")]
    public static int SyncInlineThreadProbe()
    {
        return Environment.CurrentManagedThreadId;
    }

    [AspireExport("syncBackgroundThreadProbe", Description = "Captures the current thread for background-thread sync invocation", RunSyncOnBackgroundThread = true)]
    public static int SyncBackgroundThreadProbe()
    {
        return Environment.CurrentManagedThreadId;
    }

    [AspireExport("valueTaskBackgroundThreadProbe", Description = "Captures the current thread for ValueTask<T> invocation", RunSyncOnBackgroundThread = true)]
    public static ValueTask<int> ValueTaskBackgroundThreadProbe()
    {
        return ValueTask.FromResult(Environment.CurrentManagedThreadId);
    }

    [AspireExport("nonGenericValueTaskBackgroundThreadProbe", Description = "Captures the current thread for ValueTask invocation", RunSyncOnBackgroundThread = true)]
    public static ValueTask NonGenericValueTaskBackgroundThreadProbe()
    {
        LastObservedThreadId = Environment.CurrentManagedThreadId;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Test capabilities for Array, Union type, List, and Dict marshalling.
/// </summary>
internal static class TestTypeCategoryCapabilities
{
    [AspireExport("sumArray", Description = "Sums an integer array")]
    public static int SumArray(int[] values)
    {
        return values.Sum();
    }

    [AspireExport("returnArray", Description = "Returns a string array")]
    public static string[] ReturnArray(int count)
    {
        return Enumerable.Range(0, count).Select(i => $"item{i}").ToArray();
    }

    [AspireExport("acceptReadOnlyList", Description = "Accepts a readonly list")]
    public static int SumReadOnlyList(IReadOnlyList<int> values)
    {
        return values.Sum();
    }

    [AspireExport("acceptUnion", Description = "Accepts a union of string or int")]
    public static string AcceptUnion([AspireUnion(typeof(string), typeof(int))] object value)
    {
        return value.ToString()!;
    }

    [AspireExport("returnMutableList", Description = "Returns a mutable List<string>")]
    public static List<object> ReturnMutableList()
    {
        return ["first", "second", "third"];
    }

    [AspireExport("returnMutableDict", Description = "Returns a mutable Dictionary<string, object>")]
    public static Dictionary<string, object> ReturnMutableDict()
    {
        return new Dictionary<string, object>
        {
            ["key1"] = "value1",
            ["key2"] = 42
        };
    }
}

/// <summary>
/// Test enum for enum dispatch tests.
/// </summary>
internal enum TestDispatchEnum
{
    ValueA,
    ValueB,
    ValueC
}

/// <summary>
/// Test capabilities for enum dispatch.
/// </summary>
internal static class TestEnumCapabilities
{
    [AspireExport("acceptEnum", Description = "Accepts an enum parameter")]
    public static string AcceptEnum(TestDispatchEnum value)
    {
        return $"Received: {value}";
    }

    [AspireExport("returnEnum", Description = "Returns an enum value")]
    public static TestDispatchEnum ReturnEnum(string name)
    {
        return Enum.Parse<TestDispatchEnum>(name);
    }

    [AspireExport("acceptOptionalEnum", Description = "Accepts an optional enum parameter")]
    public static string AcceptOptionalEnum(TestDispatchEnum? value = null)
    {
        return value.HasValue ? $"Received: {value.Value}" : "No value";
    }
}

/// <summary>
/// Test DTO with an enum property for GetDto deserialization tests.
/// </summary>
internal sealed class TestDtoWithEnum
{
    public string? Label { get; set; }
    public TestDispatchEnum Status { get; set; }
}

/// <summary>
/// A minimal IResourceBuilder implementation for testing builder-to-resource unwrapping.
/// </summary>
internal sealed class TestResourceBuilder<T> : IResourceBuilder<T> where T : IResource
{
    public TestResourceBuilder(T resource)
    {
        Resource = resource;
    }

    public T Resource { get; }
    public IDistributedApplicationBuilder ApplicationBuilder => throw new NotImplementedException();

    public IResourceBuilder<T> WithAnnotation<TAnnotation>(TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append) where TAnnotation : IResourceAnnotation
        => throw new NotImplementedException();
}

/// <summary>
/// Test resource type with properties that should be accessible via builder handles.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class TestResourceWithProperties : Resource
{
    public TestResourceWithProperties(string name) : base(name) { }

    public string Color { get; set; } = "default";
}

/// <summary>
/// Test resource type with instance methods that should be accessible via builder handles.
/// </summary>
[AspireExport(ExposeMethods = true)]
internal sealed class TestResourceWithMethods : Resource
{
    public TestResourceWithMethods(string name) : base(name) { }

    public string Greet(string prefix)
    {
        return $"{prefix}, {Name}!";
    }
}
