// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEventHubConnectionPropertiesTests
{
    [Fact]
    public void AzureEventHubResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs");
        var eventHub = eventHubs.AddHub("eventhub", "myhub");

        var resource = Assert.Single(builder.Resources.OfType<AzureEventHubResource>());
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
            });
    }
}
