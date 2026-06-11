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
}
