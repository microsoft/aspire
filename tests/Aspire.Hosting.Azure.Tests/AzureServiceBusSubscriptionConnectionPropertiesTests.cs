// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureServiceBusSubscriptionConnectionPropertiesTests
{
    [Fact]
    public void AzureServiceBusSubscriptionResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("servicebus");
        var topic = serviceBus.AddServiceBusTopic("topic", "mytopic");
        var subscription = topic.AddServiceBusSubscription("subscription", "mysubscription");

        var resource = Assert.Single(builder.Resources.OfType<AzureServiceBusSubscriptionResource>());
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
                Assert.Equal("TopicName", property.Key);
                Assert.Equal("mytopic", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("SubscriptionName", property.Key);
                Assert.Equal("mysubscription", property.Value.ValueExpression);
            });
    }
}
