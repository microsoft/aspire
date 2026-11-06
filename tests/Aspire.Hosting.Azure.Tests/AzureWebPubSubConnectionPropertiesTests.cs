// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureWebPubSubConnectionPropertiesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AzureWebPubSubResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
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

    [Fact]
    public void AzureWebPubSubHubResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webpubsub = builder.AddAzureWebPubSub("webpubsub");
        var hub = webpubsub.AddHub("chat");

        var properties = ((IResourceWithConnectionString)hub.Resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{webpubsub.outputs.endpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("HubName", property.Key);
                Assert.Equal("chat", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureWebPubSubHubResourceGetConnectionPropertiesWithEmulatorReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webpubsub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator();
        var hub = webpubsub.AddHub("chat");

        var properties = ((IResourceWithConnectionString)hub.Resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{webpubsub.bindings.emulator.url}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("HubName", property.Key);
                Assert.Equal("chat", property.Value.ValueExpression);
            });
    }
}
