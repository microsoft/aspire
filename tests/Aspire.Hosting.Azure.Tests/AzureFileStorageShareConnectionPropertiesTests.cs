// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFileStorageShareConnectionPropertiesTests
{
    [Fact]
    public void AzureFileStorageShareResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var files = storage.AddFiles("files");
        var share = files.AddFileShare("share", "myshare");

        var properties = ((IResourceWithConnectionString)share.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.fileEndpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("FileShareName", property.Key);
                Assert.Equal("myshare", property.Value.ValueExpression);
            });
    }
}
