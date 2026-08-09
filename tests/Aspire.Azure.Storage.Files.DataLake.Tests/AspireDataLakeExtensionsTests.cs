// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Storage.Files.DataLake.Tests;

public sealed class AspireDataLakeExtensionsTests
{
    private const string ConnectionName = "data-lake";
    private const string AccountName = "aspirestoragetests";
    private const string ConnectionString = $"AccountName={AccountName};AccountKey=fake";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataLakeReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>($"ConnectionStrings:{ConnectionName}", ConnectionString)]);

        if (useKeyed)
        {
            builder.AddKeyedAzureDataLakeServiceClient(ConnectionName);
        }
        else
        {
            builder.AddAzureDataLakeServiceClient(ConnectionName);
        }

        using var host = builder.Build();
        var client = useKeyed
            ? host.Services.GetRequiredKeyedService<DataLakeServiceClient>(ConnectionName)
            : host.Services.GetRequiredService<DataLakeServiceClient>();

        Assert.Equal(AccountName, client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataLakeConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("ConnectionStrings:unused", ConnectionString)]);

        if (useKeyed)
        {
            builder.AddKeyedAzureDataLakeServiceClient(
                ConnectionName,
                settings => settings.ConnectionString = ConnectionString);
        }
        else
        {
            builder.AddAzureDataLakeServiceClient(
                ConnectionName,
                settings => settings.ConnectionString = ConnectionString);
        }

        using var host = builder.Build();
        var client = useKeyed
            ? host.Services.GetRequiredKeyedService<DataLakeServiceClient>(ConnectionName)
            : host.Services.GetRequiredService<DataLakeServiceClient>();

        Assert.Equal(AccountName, client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? ConnectionName : null;
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(
                ConformanceTests.CreateConfigKey("Aspire:Azure:Storage:Files:DataLake", key, "ServiceUri"),
                "unused"),
            new KeyValuePair<string, string?>($"ConnectionStrings:{ConnectionName}", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureDataLakeServiceClient(ConnectionName);
        }
        else
        {
            builder.AddAzureDataLakeServiceClient(ConnectionName);
        }

        using var host = builder.Build();
        var client = useKeyed
            ? host.Services.GetRequiredKeyedService<DataLakeServiceClient>(ConnectionName)
            : host.Services.GetRequiredService<DataLakeServiceClient>();

        Assert.Equal(AccountName, client.AccountName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceUriWorksInConnectionStrings(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>($"ConnectionStrings:{ConnectionName}", ConformanceTests.ServiceUri)]);

        if (useKeyed)
        {
            builder.AddKeyedAzureDataLakeServiceClient(ConnectionName);
        }
        else
        {
            builder.AddAzureDataLakeServiceClient(ConnectionName);
        }

        using var host = builder.Build();
        var client = useKeyed
            ? host.Services.GetRequiredKeyedService<DataLakeServiceClient>(ConnectionName)
            : host.Services.GetRequiredService<DataLakeServiceClient>();

        Assert.Equal(AccountName, client.AccountName);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>($"ConnectionStrings:{ConnectionName}1", ConformanceTests.ServiceUri),
            new KeyValuePair<string, string?>(
                $"ConnectionStrings:{ConnectionName}2",
                $"https://{AccountName}2.dfs.core.windows.net/"),
            new KeyValuePair<string, string?>(
                $"ConnectionStrings:{ConnectionName}3",
                $"https://{AccountName}3.dfs.core.windows.net/")
        ]);

        builder.AddAzureDataLakeServiceClient($"{ConnectionName}1");
        builder.AddKeyedAzureDataLakeServiceClient($"{ConnectionName}2");
        builder.AddKeyedAzureDataLakeServiceClient($"{ConnectionName}3");

        using var host = builder.Build();

        // Unkeyed services don't work with keyed services. See https://github.com/microsoft/aspire/issues/3890
        //var client1 = host.Services.GetRequiredService<DataLakeServiceClient>();
        var client2 = host.Services.GetRequiredKeyedService<DataLakeServiceClient>($"{ConnectionName}2");
        var client3 = host.Services.GetRequiredKeyedService<DataLakeServiceClient>($"{ConnectionName}3");

        //Assert.NotSame(client1, client2);
        //Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);

        //Assert.Equal($"{AccountName}", client1.AccountName);
        Assert.Equal($"{AccountName}2", client2.AccountName);
        Assert.Equal($"{AccountName}3", client3.AccountName);
    }
}
