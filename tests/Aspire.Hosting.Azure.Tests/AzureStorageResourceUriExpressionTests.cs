// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureStorageResourceUriExpressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlobUriExpressionReturnsExpectedValue(bool isEmulator)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var storage = builder.AddAzureStorage("storage");
        if (isEmulator)
        {
            storage.RunAsEmulator();
        }

        var resource = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        var uriExpression = resource.BlobUriExpression;
        Assert.Equal(
            isEmulator ? "{storage.bindings.blob.url}" : "{storage.outputs.blobEndpoint}",
            uriExpression.ValueExpression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataLakeUriExpressionReturnsExpectedValueOrThrowUnderEmulator(bool isEmulator)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var storage = builder.AddAzureStorage("storage");
        if (isEmulator)
        {
            storage.RunAsEmulator();
        }

        var resource = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        if (isEmulator)
        {
            Assert.Throws<InvalidOperationException>(() => resource.DataLakeUriExpression);
        }
        else
        {
            Assert.Equal("{storage.outputs.dataLakeEndpoint}", resource.DataLakeUriExpression.ValueExpression);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueUriExpressionReturnsExpectedValue(bool isEmulator)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var storage = builder.AddAzureStorage("storage");
        if (isEmulator)
        {
            storage.RunAsEmulator();
        }

        var resource = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        var uriExpression = resource.QueueUriExpression;
        Assert.Equal(
            isEmulator ? "{storage.bindings.queue.url}" : "{storage.outputs.queueEndpoint}",
            uriExpression.ValueExpression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TableUriExpressionReturnsExpectedValue(bool isEmulator)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var storage = builder.AddAzureStorage("storage");
        if (isEmulator)
        {
            storage.RunAsEmulator();
        }

        var resource = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        var uriExpression = resource.TableUriExpression;
        Assert.Equal(
            isEmulator ? "{storage.bindings.table.url}" : "{storage.outputs.tableEndpoint}",
            uriExpression.ValueExpression);
    }
}
