// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureQueueStorageQueueConnectionPropertiesTests
{
    [Fact]
    public void AzureQueueStorageQueueResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var queue = storage.AddQueue("queue", "myqueue");

        var resource = Assert.Single(builder.Resources.OfType<AzureQueueStorageQueueResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.queueEndpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("QueueName", property.Key);
                Assert.Equal("myqueue", property.Value.ValueExpression);
            });
    }
}
