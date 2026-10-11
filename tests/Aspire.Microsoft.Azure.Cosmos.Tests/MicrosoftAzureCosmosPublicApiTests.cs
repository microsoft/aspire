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
    public void AddAzureCosmosClientWithNullCallbacksShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        var action = () => builder.AddAzureCosmosClient("cosmos", null, null);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
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
    [InlineData("client", true)]
    [InlineData("client", false)]
    [InlineData("container", true)]
    [InlineData("container", false)]
    [InlineData("database", true)]
    [InlineData("database", false)]
    public void AddKeyedAzureCosmosMethodsShouldThrowWhenNameIsNullOrEmpty(string registrationType, bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;

        Action action = registrationType switch
        {
            "client" => () => builder.AddKeyedAzureCosmosClient(name),
            "container" => () => builder.AddKeyedAzureCosmosContainer(name),
            "database" => () => builder.AddKeyedAzureCosmosDatabase(name),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("container")]
    [InlineData("database")]
    public void ProviderAwareCallbacksShouldThrowWhenBuilderIsNull(string registrationType)
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "cosmos";
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null;
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions = null;

        Action action = registrationType switch
        {
            "client" => () => builder.AddAzureCosmosClient(connectionName, configureSettings, configureClientOptions),
            "container" => () => builder.AddAzureCosmosContainer(connectionName, configureSettings, configureClientOptions),
            "database" => () => builder.AddAzureCosmosDatabase(connectionName, configureSettings, configureClientOptions),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("container")]
    [InlineData("database")]
    public void KeyedProviderAwareCallbacksShouldThrowWhenBuilderIsNull(string registrationType)
    {
        IHostApplicationBuilder builder = null!;
        const string name = "cosmos";
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null;
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions = null;

        Action action = registrationType switch
        {
            "client" => () => builder.AddKeyedAzureCosmosClient(name, configureSettings, configureClientOptions),
            "container" => () => builder.AddKeyedAzureCosmosContainer(name, configureSettings, configureClientOptions),
            "database" => () => builder.AddKeyedAzureCosmosDatabase(name, configureSettings, configureClientOptions),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }
}
