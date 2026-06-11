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
}
