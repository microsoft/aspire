// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureServiceBusQueueConnectionPropertiesTests
{
    [Fact]
    public void AzureServiceBusQueueResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("servicebus");
        var queue = serviceBus.AddServiceBusQueue("queue", "myqueue");

        var resource = Assert.Single(builder.Resources.OfType<AzureServiceBusQueueResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

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
            },
            property =>
            {
                Assert.Equal("QueueName", property.Key);
                Assert.Equal("myqueue", property.Value.ValueExpression);
            });
    }
}
