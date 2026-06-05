// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureBlobStorageContainerConnectionPropertiesTests
{
    [Fact]
    public void AzureBlobStorageContainerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var container = storage.AddBlobContainer("container", "mycontainer");

        var resource = Assert.Single(builder.Resources.OfType<AzureBlobStorageContainerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.blobEndpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("BlobContainerName", property.Key);
                Assert.Equal("mycontainer", property.Value.ValueExpression);
            });
    }
}
