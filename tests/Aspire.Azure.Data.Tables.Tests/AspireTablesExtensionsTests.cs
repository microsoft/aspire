// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Data.Tables.Tests;

public class AspireTablesExtensionsTests
{
    private const string ConnectionString = "AccountName=aspirestoragetests;AccountKey=fake";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:tables", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureTableServiceClient("tables");
        }
        else
        {
            builder.AddAzureTableServiceClient("tables");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<TableServiceClient>("tables") :
            host.Services.GetRequiredService<TableServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:tables", "AccountName=unused;AccountKey=myAccountKey")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureTableServiceClient("tables", settings => settings.ConnectionString = ConnectionString);
        }
        else
        {
            builder.AddAzureTableServiceClient("tables", settings => settings.ConnectionString = ConnectionString);
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<TableServiceClient>("tables") :
            host.Services.GetRequiredService<TableServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "tables" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Azure:Data:Tables", key, "ServiceUri"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:tables", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureTableServiceClient("tables");
        }
        else
        {
            builder.AddAzureTableServiceClient("tables");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<TableServiceClient>("tables") :
            host.Services.GetRequiredService<TableServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceUriWorksInConnectionStrings(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:tables", ConformanceTests.ServiceUri)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureTableServiceClient("tables");
        }
        else
        {
            builder.AddAzureTableServiceClient("tables");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<TableServiceClient>("tables") :
            host.Services.GetRequiredService<TableServiceClient>();

        Assert.Equal("aspirestoragetests", client.AccountName);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:tables1", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:tables2", "AccountName=account2;AccountKey=fake"),
            new KeyValuePair<string, string?>("ConnectionStrings:tables3", "AccountName=account3;AccountKey=fake")
        ]);

        builder.AddAzureTableServiceClient("tables1");
        builder.AddKeyedAzureTableServiceClient("tables2");
        builder.AddKeyedAzureTableServiceClient("tables3");

        using var host = builder.Build();

        // Unkeyed services don't work with keyed services. See https://github.com/microsoft/aspire/issues/3890
        //var client1 = host.Services.GetRequiredService<TableServiceClient>();
        var client2 = host.Services.GetRequiredKeyedService<TableServiceClient>("tables2");
        var client3 = host.Services.GetRequiredKeyedService<TableServiceClient>("tables3");

        //Assert.NotSame(client1, client2);
        //Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);

        //Assert.Equal("aspirestoragetests", client1.AccountName);
        Assert.Equal("account2", client2.AccountName);
        Assert.Equal("account3", client3.AccountName);
    }
}
