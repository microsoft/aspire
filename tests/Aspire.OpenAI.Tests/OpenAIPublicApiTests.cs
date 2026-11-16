// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.OpenAI.Tests;

public class OpenAIPublicApiTests
{
    [Fact]
    public void CtorAspireOpenAIClientBuilderShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder hostBuilder = null!;
        const string connectionName = "open-ai";
        const string? serviceKey = null;
        bool disableTracing = false;
        bool disableMetrics = false;
        bool enableSensitiveTelemetryData = false;

        var action = () => new AspireOpenAIClientBuilder(hostBuilder, connectionName, serviceKey, disableTracing, disableMetrics, enableSensitiveTelemetryData);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(hostBuilder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorAspireOpenAIClientBuilderShouldThrowWhenConnectionNameIsNullOrEmpty(bool isNull)
    {
        var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;
        const string? serviceKey = null;
        bool disableTracing = false;
        bool disableMetrics = false;
        bool enableSensitiveTelemetryData = false;

        var action = () => new AspireOpenAIClientBuilder(hostBuilder, connectionName, serviceKey, disableTracing, disableMetrics, enableSensitiveTelemetryData);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Fact]
    public void AddChatClientShouldThrowWhenBuilderIsNull()
    {
        AspireOpenAIClientBuilder builder = null!;

        var action = () => builder.AddChatClient();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddKeyedChatClientShouldThrowWhenBuilderIsNull()
    {
        AspireOpenAIClientBuilder builder = null!;
        const string serviceKey = "chat";

        var action = () => builder.AddKeyedChatClient(serviceKey);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyedChatClientShouldThrowWhenServiceKeyIsNullOrEmpty(bool isNull)
    {
        var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
        const string connectionName = "open-ai";
        const string? hostServiceKey = null;
        bool disableTracing = false;
        bool disableMetrics = false;
        bool enableSensitiveTelemetryData = false;
        var builder = new AspireOpenAIClientBuilder(hostBuilder, connectionName, hostServiceKey, disableTracing, disableMetrics, enableSensitiveTelemetryData);
        var serviceKey = isNull ? null! : string.Empty;

        var action = () => builder.AddKeyedChatClient(serviceKey);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(serviceKey), exception.ParamName);
    }

    [Fact]
    public void AddEmbeddingGeneratorShouldThrowWhenBuilderIsNull()
    {
        AspireOpenAIClientBuilder builder = null!;

        var action = () => builder.AddEmbeddingGenerator();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddKeyedEmbeddingGeneratorShouldThrowWhenBuilderIsNull()
    {
        AspireOpenAIClientBuilder builder = null!;
        const string serviceKey = "generator";

        var action = () => builder.AddKeyedEmbeddingGenerator(serviceKey);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyedEmbeddingGeneratorShouldThrowWhenServiceKeyIsNullOrEmpty(bool isNull)
    {
        var hostBuilder = Host.CreateEmptyApplicationBuilder(null);
        const string connectionName = "open-ai";
        const string? hostServiceKey = null;
        bool disableTracing = false;
        bool disableMetrics = false;
        bool enableSensitiveTelemetryData = false;
        var builder = new AspireOpenAIClientBuilder(hostBuilder, connectionName, hostServiceKey, disableTracing, disableMetrics, enableSensitiveTelemetryData);
        var serviceKey = isNull ? null! : string.Empty;

        var action = () => builder.AddKeyedEmbeddingGenerator(serviceKey);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(serviceKey), exception.ParamName);
    }

    [Fact]
    public void AddOpenAIClientShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "open-ai";

        var action = () => builder.AddOpenAIClient(connectionName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddOpenAIClientShouldThrowWhenConnectionNameIsNullOrEmpty(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;

        var action = () => builder.AddOpenAIClient(connectionName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Fact]
    public void AddKeyedOpenAIClientShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;
        const string name = "open-ai";

        var action = () => builder.AddKeyedOpenAIClient(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyedOpenAIClientShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddKeyedOpenAIClient(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
