// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Search.Documents.Tests;

public class AspireAzureSearchExtensionsTests
{
    private const string SearchEndpoint = "https://aspireazuresearchtests.search.windows.net/";
    private const string ConnectionString = $"Endpoint={SearchEndpoint};Key=fake";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:search", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureSearchClient("search");
        }
        else
        {
            builder.AddAzureSearchClient("search");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<SearchIndexClient>("search") :
            host.Services.GetRequiredService<SearchIndexClient>();

        Assert.NotNull(client);
        Assert.Equal(new Uri(SearchEndpoint), client.Endpoint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var searchEndpoint = new Uri("https://aspireazuresearchtests.search.windows.net/");
        var key = "fake";
        var builder = Host.CreateEmptyApplicationBuilder(null);

        if (useKeyed)
        {
            builder.AddKeyedAzureSearchClient("search", settings => { settings.Endpoint = searchEndpoint; settings.Key = key; });
        }
        else
        {
            builder.AddAzureSearchClient("search", settings => { settings.Endpoint = searchEndpoint; settings.Key = key; });
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<SearchIndexClient>("search") :
            host.Services.GetRequiredService<SearchIndexClient>();

        Assert.NotNull(client);
        Assert.Equal(new Uri(SearchEndpoint), client.Endpoint);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:search1", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:search2", "Endpoint=https://aspireazuresearchtests2.search.windows.net/;Key=fake"),
            new KeyValuePair<string, string?>("ConnectionStrings:search3", "Endpoint=https://aspireazuresearchtests3.search.windows.net/;Key=fake")
        ]);

        builder.AddAzureSearchClient("search1");
        builder.AddKeyedAzureSearchClient("search2");
        builder.AddKeyedAzureSearchClient("search3");

        using var host = builder.Build();

        // Unkeyed services don't work with keyed services. See https://github.com/microsoft/aspire/issues/3890
        //var client1 = host.Services.GetRequiredService<SearchIndexClient>();
        var client2 = host.Services.GetRequiredKeyedService<SearchIndexClient>("search2");
        var client3 = host.Services.GetRequiredKeyedService<SearchIndexClient>("search3");

        //Assert.NotSame(client1, client2);
        //Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);

        //Assert.Equal(new Uri(SearchEndpoint), client1.Endpoint);
        Assert.Equal(new Uri("https://aspireazuresearchtests2.search.windows.net/"), client2.Endpoint);
        Assert.Equal(new Uri("https://aspireazuresearchtests3.search.windows.net/"), client3.Endpoint);
    }
}
