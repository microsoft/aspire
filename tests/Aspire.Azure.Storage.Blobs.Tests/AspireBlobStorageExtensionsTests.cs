// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Storage.Blobs.Tests;

public class AspireBlobStorageExtensionsTests
{
    private const string ConnectionString = "AccountName=aspirestoragetests;AccountKey=fake";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:blob", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureBlobServiceClient("blob");
        }
        else
        {
            builder.AddAzureBlobServiceClient("blob");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<BlobServiceClient>("blob") :
            host.Services.GetRequiredService<BlobServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:blob", "AccountName=unused;AccountKey=fake")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureBlobServiceClient("blob", settings => settings.ConnectionString = ConnectionString);
        }
        else
        {
            builder.AddAzureBlobServiceClient("blob", settings => settings.ConnectionString = ConnectionString);
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<BlobServiceClient>("blob") :
            host.Services.GetRequiredService<BlobServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "blob" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Azure:Storage:Blobs", key, "ServiceUri"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:blob", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureBlobServiceClient("blob");
        }
        else
        {
            builder.AddAzureBlobServiceClient("blob");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<BlobServiceClient>("blob") :
            host.Services.GetRequiredService<BlobServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceUriWorksInConnectionStrings(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:blob", ConformanceTests.ServiceUri)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureBlobServiceClient("blob");
        }
        else
        {
            builder.AddAzureBlobServiceClient("blob");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<BlobServiceClient>("blob") :
            host.Services.GetRequiredService<BlobServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:blob1", ConformanceTests.ServiceUri),
            new KeyValuePair<string, string?>("ConnectionStrings:blob2", "https://aspirestoragetests2.blob.core.windows.net/"),
            new KeyValuePair<string, string?>("ConnectionStrings:blob3", "https://aspirestoragetests3.blob.core.windows.net/")
        ]);

        builder.AddAzureBlobServiceClient("blob1");
        builder.AddKeyedAzureBlobServiceClient("blob2");
        builder.AddKeyedAzureBlobServiceClient("blob3");

        using var host = builder.Build();

        // Unkeyed services don't work with keyed services. See https://github.com/microsoft/aspire/issues/3890
        //var client1 = host.Services.GetRequiredService<BlobServiceClient>();
        var client2 = host.Services.GetRequiredKeyedService<BlobServiceClient>("blob2");
        var client3 = host.Services.GetRequiredKeyedService<BlobServiceClient>("blob3");

        //Assert.NotSame(client1, client2);
        //Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);

        //Assert.Equal("aspirestoragetests", client1.AccountName);
        Assert.Equal("aspirestoragetests2", client2.AccountName);
        Assert.Equal("aspirestoragetests3", client3.AccountName);
    }
}
