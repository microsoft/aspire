// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureServiceBusConnectionPropertiesTests
{
    [Fact]
    public void AzureServiceBusResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("servicebus");

        var properties = ((IResourceWithConnectionString)serviceBus.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{servicebus.outputs.serviceBusHostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{servicebus.outputs.serviceBusEndpoint}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureServiceBusResourceGetConnectionPropertiesReturnsConnectionStringForEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();

        var provider = serviceBus.Resource.GetEffectiveCapability<IResourceWithConnectionString>();
        var projection = Assert.IsType<AzureServiceBusEmulatorResource>(provider);
        var properties = provider.GetConnectionProperties().ToArray();

        Assert.Same(serviceBus.Resource, projection.GetOwnerOrSelf());
        Assert.Equal(
            "Endpoint=sb://{servicebus.bindings.emulator.host}:{servicebus.bindings.emulator.port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
            provider.ConnectionStringExpression.ValueExpression);
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{servicebus.bindings.emulator.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{servicebus.bindings.emulator.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("sb://{servicebus.bindings.emulator.host}:{servicebus.bindings.emulator.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal("Endpoint=sb://{servicebus.bindings.emulator.host}:{servicebus.bindings.emulator.port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", property.Value.ValueExpression);
            });
        Assert.Equal(provider.ConnectionStringExpression.ValueExpression, serviceBus.Resource.ConnectionStringExpression.ValueExpression);
    }
}
