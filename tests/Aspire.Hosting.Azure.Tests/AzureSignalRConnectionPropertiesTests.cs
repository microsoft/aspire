// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSignalRConnectionPropertiesTests
{
    [Fact]
    public void AzureSignalRResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var signalr = builder.AddAzureSignalR("signalr");

        var properties = ((IResourceWithConnectionString)signalr.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("https://{signalr.outputs.hostName}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureSignalREmulatorProjectionProvidesConnectionStringAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var signalr = builder.AddAzureSignalR("signalr").RunAsEmulator();

        var provider = signalr.Resource.GetEffectiveCapability<IResourceWithConnectionString>();
        var projection = Assert.IsType<AzureSignalREmulatorResource>(provider);
        var properties = provider.GetConnectionProperties().ToArray();

        Assert.Same(signalr.Resource, projection.GetOwnerOrSelf());
        Assert.Equal(
            "Endpoint={signalr.bindings.emulator.url};AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;",
            provider.ConnectionStringExpression.ValueExpression);
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{signalr.bindings.emulator.url}", property.Value.ValueExpression);
            });
        Assert.Equal(provider.ConnectionStringExpression.ValueExpression, signalr.Resource.ConnectionStringExpression.ValueExpression);
    }
}
