// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddRabbitMQShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "rabbitMQ";

        var action = () => builder.AddRabbitMQ(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddRabbitMQShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddRabbitMQ(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;
        const string source = "/var/lib/rabbitmq";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var source = isNull ? null! : string.Empty;

        var action = () => rabbitMQ.WithDataBindMount(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithManagementPluginShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.WithManagementPlugin();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithManagementPluginWithPortShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;
        const int port = 15672;

        var action = () => builder.WithManagementPlugin(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorRabbitMQServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        const string passwordValue = nameof(passwordValue);
        ParameterResource? userName = null;
        var password = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, passwordValue);

        var action = () => new RabbitMQServerResource(name: name, userName: userName, password: password);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorRabbitMQServerResourceShouldThrowWhenPasswordIsNull()
    {
        string name = "rabbitMQ";
        ParameterResource? userName = null;
        ParameterResource password = null!;

        var action = () => new RabbitMQServerResource(name: name, userName: userName, password: password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(password), exception.ParamName);
    }

    [Fact]
    public void AddVirtualHostShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.AddVirtualHost("vhost");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddVirtualHostShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var name = isNull ? null! : string.Empty;

        var action = () => rabbitMQ.AddVirtualHost(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddQueueOnVhostShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQVirtualHostResource> builder = null!;

        var action = () => builder.AddQueue("queue");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddQueueOnVhostShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var vhost = rabbitMQ.AddVirtualHost("vhost");
        var name = isNull ? null! : string.Empty;

        var action = () => vhost.AddQueue(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddQueueOnServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.AddQueue("queue");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddExchangeOnVhostShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQVirtualHostResource> builder = null!;

        var action = () => builder.AddExchange("exchange");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddExchangeOnVhostShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var vhost = rabbitMQ.AddVirtualHost("vhost");
        var name = isNull ? null! : string.Empty;

        var action = () => vhost.AddExchange(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddExchangeOnServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.AddExchange("exchange");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithQueuePropertiesShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQQueueResource> builder = null!;

        var action = () => builder.WithProperties(q => q.Durable = true);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithQueuePropertiesShouldThrowWhenConfigureIsNull()
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var queue = rabbitMQ.AddQueue("queue");
        Action<RabbitMQQueueResource> configure = null!;

        var action = () => queue.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void WithExchangePropertiesShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQExchangeResource> builder = null!;

        var action = () => builder.WithProperties(e => e.Durable = true);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithBindingShouldThrowWhenExchangeBuilderIsNull()
    {
        IResourceBuilder<RabbitMQExchangeResource> exchange = null!;
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var vhost = rabbitMQ.AddVirtualHost("vhost");
        var queue = vhost.AddQueue("queue");

        var action = () => exchange.WithBinding(queue);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(exchange), exception.ParamName);
    }

    [Fact]
    public void WithBindingShouldThrowWhenDestinationIsNull()
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var vhost = rabbitMQ.AddVirtualHost("vhost");
        var exchange = vhost.AddExchange("exchange");
        IResourceBuilder<RabbitMQQueueResource> destination = null!;

        var action = () => exchange.WithBinding(destination);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(destination), exception.ParamName);
    }

    [Fact]
    public void WithPluginEnumShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.WithPlugin(RabbitMQPlugin.Prometheus);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPluginStringShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<RabbitMQServerResource> builder = null!;

        var action = () => builder.WithPlugin("rabbitmq_prometheus");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithPluginStringShouldThrowWhenPluginNameIsNullOrWhiteSpace(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");
        var pluginName = isNull ? null! : " ";

        var action = () => rabbitMQ.WithPlugin(pluginName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(pluginName), exception.ParamName);
    }
}
