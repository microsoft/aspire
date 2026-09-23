// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class ServiceBusPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void CtorAzureServiceBusEmulatorResourceShouldThrowWhenInnerResourceIsNull()
    {
        AzureServiceBusResource innerResource = null!;

        var action = () => new AzureServiceBusEmulatorResource(innerResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(innerResource), exception.ParamName);
    }

    [Fact]
    public void AddAzureServiceBusShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "service-bus";

        var action = () => builder.AddAzureServiceBus(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureServiceBusShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureServiceBus(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddServiceBusQueue instead to add an Azure Service Bus Queue.")]
    public void AddQueueShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusResource> builder = null!;
        const string name = "service-queue";

        var action = () => builder.AddQueue(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddServiceBusQueue instead to add an Azure Service Bus Queue.")]
    public void AddQueueShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddQueue(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddServiceBusQueueShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusResource> builder = null!;
        const string name = "service-queue";

        var action = () => builder.AddServiceBusQueue(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddServiceBusQueueShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddServiceBusQueue(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusQueueResource> builder = null!;
        Action<AzureServiceBusQueueResource> configure = (_) => { };

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesShouldThrowWhenConfigureIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus").AddServiceBusQueue("service-queue");
        Action<AzureServiceBusQueueResource> configure = null!;

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void AddServiceBusTopicShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusResource> builder = null!;
        const string name = "topic";

        var action = () => builder.AddServiceBusTopic(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddServiceBusTopicShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddServiceBusTopic(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesTopicShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusTopicResource> builder = null!;
        Action<AzureServiceBusTopicResource> configure = (_) => { };

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesTopicShouldThrowWhenConfigureIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus").AddServiceBusTopic("service-topic");
        Action<AzureServiceBusTopicResource> configure = null!;

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void AddServiceBusSubscriptionShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusTopicResource> builder = null!;
        const string name = "topic";

        var action = () => builder.AddServiceBusSubscription(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddServiceBusSubscriptionShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus")
            .AddServiceBusTopic("service-topic");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddServiceBusSubscription(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesSubscriptionShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusSubscriptionResource> builder = null!;
        Action<AzureServiceBusSubscriptionResource> configure = (_) => { };

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesSubscriptionShouldThrowWhenConfigureIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus")
            .AddServiceBusTopic("service-topic")
            .AddServiceBusSubscription("service-subscription");
        Action<AzureServiceBusSubscriptionResource> configure = null!;

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void RunAsEmulatorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusResource> builder = null!;

        var action = () => builder.RunAsEmulator();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationFileShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusEmulatorResource> builder = null!;
        const string path = "/ServiceBus_Emulator/ConfigFiles/Config.json";

        var action = () => builder.WithConfigurationFile(path);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithConfigurationFileShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus");
        var path = isNull ? null! : string.Empty;

        var action = () => builder.RunAsEmulator(configure => configure.WithConfigurationFile(path));

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(path), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusEmulatorResource> builder = null!;
        Action<JsonNode> configJson = (_) => { };

        var action = () => builder.WithConfiguration(configJson);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationShouldThrowWhenConfigJsonIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureServiceBus("service-bus");
        Action<JsonNode> configJson = null!;

        var action = () => builder.RunAsEmulator(configure => configure.WithConfiguration(configJson));

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configJson), exception.ParamName);
    }

    [Fact]
    public void WithHostPortShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureServiceBusEmulatorResource> builder = null!;
        int? port = null;

        var action = () => builder.WithHostPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }
}
