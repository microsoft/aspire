// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureWebPubSubConnectionPropertiesTests
{
    [Fact]
    public void AzureWebPubSubResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var webpubsub = builder.AddAzureWebPubSub("webpubsub");

        var resource = Assert.Single(builder.Resources.OfType<AzureWebPubSubResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{webpubsub.outputs.endpoint}", property.Value.ValueExpression);
            });
    }
}
