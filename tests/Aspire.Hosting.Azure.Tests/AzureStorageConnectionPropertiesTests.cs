// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureStorageConnectionPropertiesTests
{
    [Fact]
    public void AzureBlobStorageResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        var properties = ((IResourceWithConnectionString)blobs.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.blobEndpoint}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureQueueStorageResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var queues = storage.AddQueues("queues");

        var properties = ((IResourceWithConnectionString)queues.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.queueEndpoint}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureTableStorageResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var tables = storage.AddTables("tables");

        var properties = ((IResourceWithConnectionString)tables.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.tableEndpoint}", property.Value.ValueExpression);
            });
    }
}
