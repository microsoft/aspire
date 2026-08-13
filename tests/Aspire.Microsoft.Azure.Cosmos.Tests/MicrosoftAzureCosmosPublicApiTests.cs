// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.Azure.Cosmos.Tests;

public class MicrosoftAzureCosmosPublicApiTests
{
    [Fact]
    public void AddAzureCosmosClientShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "cosmos";

        var action = () => builder.AddAzureCosmosClient(connectionName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddAzureCosmosClientShouldThrowWhenConnectionNameIsNullOrEmpty(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureCosmosClient(connectionName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Fact]
    public void AddKeyedAzureCosmosClientShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;
        const string name = "cosmos";

        var action = () => builder.AddKeyedAzureCosmosClient(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyedAzureCosmosClientShouldThrowWhenConnectionNameIsNullOrEmpty(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddKeyedAzureCosmosClient(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("container")]
    [InlineData("database")]
    public void ProviderAwareOverloadsShouldThrowWhenBuilderIsNull(string registrationType)
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "cosmos";
        Action<MicrosoftAzureCosmosSettings>? settingsCallback = null;
        Action<IServiceProvider, CosmosClientOptions>? clientOptionsCallback = null;

        Action action = registrationType switch
        {
            "client" => () => builder.AddAzureCosmosClient(connectionName, settingsCallback, clientOptionsCallback),
            "container" => () => builder.AddAzureCosmosContainer(connectionName, settingsCallback, clientOptionsCallback),
            "database" => () => builder.AddAzureCosmosDatabase(connectionName, settingsCallback, clientOptionsCallback),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("container")]
    [InlineData("database")]
    public void KeyedProviderAwareOverloadsShouldThrowWhenBuilderIsNull(string registrationType)
    {
        IHostApplicationBuilder builder = null!;
        const string name = "cosmos";
        Action<MicrosoftAzureCosmosSettings>? settingsCallback = null;
        Action<IServiceProvider, CosmosClientOptions>? clientOptionsCallback = null;

        Action action = registrationType switch
        {
            "client" => () => builder.AddKeyedAzureCosmosClient(name, settingsCallback, clientOptionsCallback),
            "container" => () => builder.AddKeyedAzureCosmosContainer(name, settingsCallback, clientOptionsCallback),
            "database" => () => builder.AddKeyedAzureCosmosDatabase(name, settingsCallback, clientOptionsCallback),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }
}
