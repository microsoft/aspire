// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class ExternalCapabilityRegistryTests
{
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

        var initialization = registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(30), cancellation.Token);
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
            await registry.InitializeAllHostsAsync(TimeSpan.Zero, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(registry.IsRegistered("test.external/pending"));
            var context = CreateContext();
            Assert.Same(context, registry.AugmentContext(context));
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/pending"));
        }
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
            registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

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
        await registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.AddIntegrationHost(secondConnection.ServerRpc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

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
        await registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
        await registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
        await registry.InitializeAllHostsAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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

    private static AtsContext CreateContext(params AtsCapabilityInfo[] capabilities)
        => new()
        {
            Capabilities = capabilities,
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = []
        };
}
