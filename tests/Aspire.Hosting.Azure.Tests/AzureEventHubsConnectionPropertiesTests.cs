// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEventHubsConnectionPropertiesTests
{
    [Fact]
    public void AzureEventHubsResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs");

        var properties = ((IResourceWithConnectionString)eventHubs.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{eventhubs.outputs.eventHubsHostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{eventhubs.outputs.eventHubsEndpoint}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureEventHubsResourceGetConnectionPropertiesReturnsConnectionStringForEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs").RunAsEmulator();

        var provider = eventHubs.Resource.GetEffectiveCapability<IResourceWithConnectionString>();
        var projection = Assert.IsType<AzureEventHubsEmulatorResource>(provider);
        var properties = provider.GetConnectionProperties().ToArray();

        Assert.Same(eventHubs.Resource, projection.GetOwnerOrSelf());
        Assert.Equal(
            "Endpoint=sb://{eventhubs.bindings.emulator.host}:{eventhubs.bindings.emulator.port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true",
            provider.ConnectionStringExpression.ValueExpression);
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{eventhubs.bindings.emulator.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{eventhubs.bindings.emulator.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("sb://{eventhubs.bindings.emulator.host}:{eventhubs.bindings.emulator.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal("Endpoint={eventhubs.bindings.emulator.host}:{eventhubs.bindings.emulator.port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true", property.Value.ValueExpression);
            });
        Assert.Equal(provider.ConnectionStringExpression.ValueExpression, eventHubs.Resource.ConnectionStringExpression.ValueExpression);
    }
}
