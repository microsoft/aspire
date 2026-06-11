// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEventHubConsumerGroupConnectionPropertiesTests
{
    [Fact]
    public void AzureEventHubConsumerGroupResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs");
        var eventHub = eventHubs.AddHub("eventhub", "myhub");
        var consumerGroup = eventHub.AddConsumerGroup("consumergroup", "mygroup");

        var resource = Assert.Single(builder.Resources.OfType<AzureEventHubConsumerGroupResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

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
            },
            property =>
            {
                Assert.Equal("EventHubName", property.Key);
                Assert.Equal("myhub", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConsumerGroupName", property.Key);
                Assert.Equal("mygroup", property.Value.ValueExpression);
            });
    }
}
