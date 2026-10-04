// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class ExternalCapabilityRegistryTests
{
    [Fact]
    public async Task Registration_RequiresTheExpectedAttemptAndRejectsDuplicates()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var connection = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var registration = registry.ExpectHostRegistration("attempt");

        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("unknown", connection.ServerRpc));
        Assert.False(registration.IsCompleted);
        registry.AddIntegrationHost("attempt", connection.ServerRpc);
        Assert.Same(connection.ServerRpc, await registration);
        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("attempt", connection.ServerRpc));
        Assert.Equal(1, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(0, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registration_ExpiredAttemptCannotRegisterForItsReplacement()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var connection = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        _ = registry.ExpectHostRegistration("old");
        registry.ForgetHostRegistration("old");
        var replacement = registry.ExpectHostRegistration("replacement");

        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("old", connection.ServerRpc));
        Assert.False(replacement.IsCompleted);
        registry.AddIntegrationHost("replacement", connection.ServerRpc);
        Assert.Same(connection.ServerRpc, await replacement);
    }

    [Fact]
    public async Task ReplaceHostAsync_RediscoveryRoutesOnlyTheRecoveredHostsCapabilities()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("original")));
        using var other = new IntegrationHostTestConnection(CreateCapabilities("test.external/other"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("other")));
        using var replacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("replacement")));
        registry.AddIntegrationHost(original.ServerRpc);
        registry.AddIntegrationHost(other.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);

        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.TryInvokeAsync("test.external/value", null));
        Assert.Equal("The integration host providing 'test.external/value' is restarting. Retry after it has registered again.", unavailable.Message);
        registry.AddIntegrationHost(replacement.ServerRpc);
        await registry.ReplaceHostAsync(original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("replacement", (await registry.TryInvokeAsync("test.external/value", null)).Result!.GetValue<string>());
        Assert.Equal("other", (await registry.TryInvokeAsync("test.external/other", null)).Result!.GetValue<string>());
        Assert.Collection(registry.AugmentContext(CreateContext()).Capabilities,
            cap => Assert.Equal("test.external/other", cap.CapabilityId),
            cap => Assert.Equal("test.external/value", cap.CapabilityId));
    }

    [Theory]
    [InlineData("test.external/different", "externalMethod", "string")]
    [InlineData("test.external/value", "differentMethod", "string")]
    [InlineData("test.external/value", "externalMethod", "int")]
    public async Task ReplaceHostAsync_RejectsChangedProjectionWithoutPublishing(string id, string method, string typeId)
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        using var replacement = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(new
        {
            capabilities = new[] { new { id, method, returnType = new { typeId, category = "Primitive" } } }
        }));
        registry.AddIntegrationHost(original.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);
        registry.AddIntegrationHost(replacement.ServerRpc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceHostAsync(
            original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.StartsWith("The restarted integration host changed its capability signatures.", error.Message);
        var capability = Assert.Single(registry.AugmentContext(CreateContext()).Capabilities);
        Assert.Equal("test.external/value", capability.CapabilityId);
        Assert.Equal("externalMethod", capability.MethodName);
        Assert.Equal("string", capability.ReturnType!.TypeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TryInvokeAsync("test.external/value", null));
    }

    [Fact]
    public async Task ReplaceHostAsync_TimesOutWithoutChangingThePublishedMetadata()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var replacement = new IntegrationHostTestConnection(_ => response.Task);
        registry.AddIntegrationHost(original.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => registry.ReplaceHostAsync(
                original.ServerRpc, replacement.ServerRpc, TimeSpan.Zero, TestContext.Current.CancellationToken));
            Assert.Equal("test.external/value", Assert.Single(registry.AugmentContext(CreateContext()).Capabilities).CapabilityId);
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/value"));
        }
    }

    [Fact]
    public async Task ReplaceHostAsync_ConcurrentRecoveriesPreserveBothHosts()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var first = new IntegrationHostTestConnection(CreateCapabilities("test.external/first"));
        using var second = new IntegrationHostTestConnection(CreateCapabilities("test.external/second"));
        using var firstReplacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/first"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("first-replacement")));
        using var secondReplacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/second"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("second-replacement")));
        registry.AddIntegrationHost(first.ServerRpc);
        registry.AddIntegrationHost(second.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(first.ServerRpc);
        registry.MarkHostUnavailable(second.ServerRpc);
        registry.AddIntegrationHost(firstReplacement.ServerRpc);
        registry.AddIntegrationHost(secondReplacement.ServerRpc);

        await Task.WhenAll(
            registry.ReplaceHostAsync(first.ServerRpc, firstReplacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            registry.ReplaceHostAsync(second.ServerRpc, secondReplacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal("first-replacement", (await registry.TryInvokeAsync("test.external/first", null)).Result!.GetValue<string>());
        Assert.Equal("second-replacement", (await registry.TryInvokeAsync("test.external/second", null)).Result!.GetValue<string>());
    }

    [Fact]
    public async Task Stop_CancelsPendingInvocationAndRemovesItsCallbackOwner()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            request.TrySetResult(args!["configure"]!.GetValue<string>());
            return response.Task;
        });
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var invocation = registry.TryInvokeAsync("test.external/callback",
            new JsonObject { ["configure"] = "guest_callback" }, new JsonRpcCallbackInvoker());
        try
        {
            var relayId = await request.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            registry.Stop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                invocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Null(registry.ResolveCallbackOwner(relayId));
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryInvokeAsync_ReusedCallbackIdsHaveIndependentOwners(bool sameGuest)
    {
        var firstRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            var isFirst = args!["name"]!.GetValue<string>() == "first";
            (isFirst ? firstRequest : secondRequest).TrySetResult(args["configure"]!.GetValue<string>());
            return (isFirst ? firstResponse : secondResponse).Task;
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var firstOwner = new JsonRpcCallbackInvoker();
        var secondOwner = sameGuest ? firstOwner : new JsonRpcCallbackInvoker();
        var firstArgs = new JsonObject { ["name"] = "first", ["configure"] = "guest_callback" };
        var secondArgs = new JsonObject { ["name"] = "second", ["configure"] = "guest_callback" };

        try
        {
            var firstInvocation = registry.TryInvokeAsync("test.external/callback", firstArgs, firstOwner);
            var firstRelayId = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var secondInvocation = registry.TryInvokeAsync("test.external/callback", secondArgs, secondOwner);
            var secondRelayId = await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotEqual(firstRelayId, secondRelayId);
            Assert.Equal("guest_callback", firstArgs["configure"]!.GetValue<string>());
            Assert.Equal("guest_callback", secondArgs["configure"]!.GetValue<string>());
            Assert.Equal((firstOwner, "guest_callback"), registry.ResolveCallbackOwner(firstRelayId));
            Assert.Equal((secondOwner, "guest_callback"), registry.ResolveCallbackOwner(secondRelayId));

            firstResponse.SetResult(null);
            Assert.True((await firstInvocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Found);
            Assert.Null(registry.ResolveCallbackOwner(firstRelayId));
            Assert.Equal((secondOwner, "guest_callback"), registry.ResolveCallbackOwner(secondRelayId));

            secondResponse.SetResult(null);
            Assert.True((await secondInvocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Found);
            Assert.Null(registry.ResolveCallbackOwner(secondRelayId));
        }
        finally
        {
            firstResponse.TrySetResult(null);
            secondResponse.TrySetResult(null);
        }
    }

    [Fact]
    public async Task TryInvokeAsync_FailedInvocationRemovesCallbackOwner()
    {
        string? relayId = null;
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            throw new InvalidOperationException("Integration callback failed.");
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RemoteInvocationException>(() => registry.TryInvokeAsync(
            "test.external/callback",
            new JsonObject { ["configure"] = "guest_callback" },
            new JsonRpcCallbackInvoker()).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.NotNull(relayId);
        Assert.Null(registry.ResolveCallbackOwner(relayId));
    }

    [Fact]
    public async Task InitializeAllHostsAsync_CancellationStopsPendingDiscovery()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(_ =>
        {
            requestStarted.TrySetResult();
            // Simulate a registered host that never responds, even to RPC cancellation.
            return response.Task;
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var initialization = registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(initialization.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                initialization.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(registry.IsRegistered("test.external/pending"));
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/pending"));
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_TimesOutPendingDiscovery()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(_ => response.Task);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        Assert.Equal(1, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));

        try
        {
            // An already-expired timeout makes this deterministic without a clock or
            // sleeps. The RPC response cannot win the race because its task stays pending.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.InitializeAllHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.False(registry.IsRegistered("test.external/pending"));
            var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
            Assert.Same(exception, augmentationException.InnerException);
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/pending"));
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_RejectsMalformedCapabilityPayloads()
    {
        foreach (var json in new[] { "null", "{}", "{\"capabilities\":null}", "{\"capabilities\":{}}", "{\"capabilities\":42}" })
        {
            using var payload = JsonDocument.Parse(json);
            using var connection = new IntegrationHostTestConnection(payload.RootElement);
            using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
            registry.AddIntegrationHost(connection.ServerRpc);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.IsType<JsonException>(exception.InnerException);
            var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
            Assert.Same(exception, augmentationException.InnerException);
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_FailedDiscoveryDoesNotExposePartialContext()
    {
        using var connection = new IntegrationHostTestConnection(_ =>
            Task.FromException<JsonElement>(new InvalidOperationException("Host discovery failed.")));
        using var successfulConnection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(new[] { new { id = "test.external/partial" } }));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        registry.AddIntegrationHost(successfulConnection.ServerRpc);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.IsType<RemoteInvocationException>(exception.InnerException);
        Assert.False(registry.IsRegistered("test.external/partial"));
        var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
        Assert.Same(exception, augmentationException.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAllHostsAsync_RejectsDuplicateExternalIdsWithoutPublishing(bool differentHosts)
    {
        using var firstConnection = new IntegrationHostTestConnection(differentHosts
            ? CreateCapabilities("test.external/first", "test.external/shared")
            : CreateCapabilities("test.external/first", "test.external/shared", "test.external/second", "test.external/shared"));
        using var secondConnection = new IntegrationHostTestConnection(
            CreateCapabilities("test.external/second", "test.external/shared"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        if (differentHosts)
        {
            registry.AddIntegrationHost(secondConnection.ServerRpc);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(differentHosts ? 2 : 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("Capability ID 'test.external/shared' is provided by multiple external registrations", exception.Message);
        Assert.False(registry.IsRegistered("test.external/first"));
        Assert.False(registry.IsRegistered("test.external/second"));
        Assert.False(registry.IsRegistered("test.external/shared"));

        var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
        Assert.Equal("Integration host capability discovery failed.", augmentationException.Message);
        Assert.Same(exception, augmentationException.InnerException);
    }

    [Fact]
    public async Task InitializeAllHostsAsync_CollisionPreservesPreviousRegistrations()
    {
        using var firstConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/existing"));
        using var secondConnection = new IntegrationHostTestConnection(
            CreateCapabilities("test.external/new", "test.external/existing"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.AddIntegrationHost(secondConnection.ServerRpc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.True(registry.IsRegistered("test.external/existing"));
        Assert.False(registry.IsRegistered("test.external/new"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AugmentContext_RejectsManagedExternalCollision(bool hasProjection)
    {
        var payload = hasProjection
            ? CreateCapabilities("test.managed/shared")
            : JsonSerializer.SerializeToElement(new[] { new { id = "test.managed/shared" } });
        using var connection = new IntegrationHostTestConnection(payload);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var managedCapability = new AtsCapabilityInfo
        {
            CapabilityId = "test.managed/shared",
            MethodName = "managedMethod",
            Parameters = [],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Boolean, Category = AtsTypeCategory.Primitive }
        };
        var context = CreateContext(managedCapability);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(context));

        Assert.Equal(
            "Capability ID 'test.managed/shared' is provided by both a managed capability and an external integration host. Capability IDs must be unique.",
            exception.Message);
        Assert.Same(managedCapability, Assert.Single(context.Capabilities));
    }

    [Fact]
    public async Task AugmentContext_PreservesManagedOrderAndSortsExternalCapabilities()
    {
        using var firstConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/z", "test.external/a"));
        using var secondConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/m"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        registry.AddIntegrationHost(secondConnection.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var managedCapability = new AtsCapabilityInfo
        {
            CapabilityId = "test.managed/z",
            MethodName = "managedMethod",
            Parameters = [],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Boolean, Category = AtsTypeCategory.Primitive }
        };
        var context = CreateContext(managedCapability);

        var augmented = registry.AugmentContext(context);

        Assert.Collection(augmented.Capabilities,
            capability => Assert.Same(managedCapability, capability),
            capability => Assert.Equal("test.external/a", capability.CapabilityId),
            capability => Assert.Equal("test.external/m", capability.CapabilityId),
            capability => Assert.Equal("test.external/z", capability.CapabilityId));
    }

    [Fact]
    public async Task AugmentContext_PreservesManagedMetadataAndExternalNullability()
    {
        using var payload = JsonDocument.Parse("""
            {
              "capabilities": [
                {
                  "id": "test.external/readValue",
                  "method": "readValue",
                  "returnType": { "typeId": "string", "category": "Primitive", "isNullable": true }
                }
              ]
            }
            """);
        using var connection = new IntegrationHostTestConnection(payload.RootElement);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var context = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "test.managed",
                    PathSegments = ["Defaults", "Name"],
                    Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive },
                    Value = JsonValue.Create("managed-value")
                }
            ]
        };
        context.Methods["test.managed/toString"] = typeof(string).GetMethod(nameof(string.ToString), Type.EmptyTypes)!;
        context.Properties["test.managed/length"] = typeof(string).GetProperty(nameof(string.Length))!;

        var augmented = registry.AugmentContext(context);

        var capability = Assert.Single(augmented.Capabilities);
        Assert.Equal("test.external/readValue", capability.CapabilityId);
        Assert.Equal("readValue", capability.MethodName);
        Assert.True(capability.ReturnType!.IsNullable);
        Assert.Same(context.ExportedValues, augmented.ExportedValues);
        Assert.Same(context.HandleTypes, augmented.HandleTypes);
        Assert.Same(context.DtoTypes, augmented.DtoTypes);
        Assert.Same(context.EnumTypes, augmented.EnumTypes);
        Assert.Same(context.Diagnostics, augmented.Diagnostics);
        Assert.Equal(context.Methods, augmented.Methods);
        Assert.Equal(context.Properties, augmented.Properties);
        Assert.Empty(context.Capabilities);
    }

    private static JsonElement CreateCapabilities(params string[] ids)
        => JsonSerializer.SerializeToElement(new
        {
            capabilities = ids.Select(id => new
            {
                id,
                method = "externalMethod",
                returnType = new { typeId = "string", category = "Primitive" }
            }).ToArray()
        });

    private static JsonElement CreateCallbackCapabilities()
        => JsonSerializer.SerializeToElement(new
        {
            capabilities = new[]
            {
                new
                {
                    id = "test.external/callback",
                    method = "externalCallback",
                    parameters = new[] { new { name = "configure", isCallback = true } },
                    returnType = new { typeId = "void", category = "Primitive" }
                }
            }
        });

    private static AtsContext CreateContext(params AtsCapabilityInfo[] capabilities)
        => new()
        {
            Capabilities = capabilities,
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = []
        };
}
