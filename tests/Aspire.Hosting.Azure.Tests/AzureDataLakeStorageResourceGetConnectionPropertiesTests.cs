// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureDataLakeStorageResourceGetConnectionPropertiesTests
{
    [Fact]
    public void AzureDataLakeStorageResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        _ = storage.AddDataLake("data-lake");

        var resource = Assert.Single(builder.Resources.OfType<AzureDataLakeStorageResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties();

        var property = Assert.Single(properties);
        Assert.Equal("Uri", property.Key);
        Assert.Equal("{storage.outputs.dataLakeEndpoint}", property.Value.ValueExpression);
    }

    [Fact]
    public void AzureDataLakeStorageResourceGetConnectionPropertiesThrowsForEmulator()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        _ = storage.AddDataLake("data-lake");

        var resource = Assert.Single(builder.Resources.OfType<AzureDataLakeStorageResource>());
        using var propertiesEnumerator =
            ((IResourceWithConnectionString)resource).GetConnectionProperties().GetEnumerator();

        Assert.Throws<InvalidOperationException>(() => propertiesEnumerator.MoveNext());
    }
}
