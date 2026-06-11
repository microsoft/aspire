// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureDataLakeStorageFileSystemConnectionPropertiesTests
{
    [Fact]
    public void AzureDataLakeStorageFileSystemResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        _ = storage.AddDataLakeFileSystem("file-system", "my-file-system");

        var resource = Assert.Single(builder.Resources.OfType<AzureDataLakeStorageFileSystemResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties()
            .ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{storage.outputs.dataLakeEndpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DataLakeFileSystemName", property.Key);
                Assert.Equal("my-file-system", property.Value.ValueExpression);
            });
    }
}
