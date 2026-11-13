// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class EventHubsPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureEventHubConsumerGroupResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddAzureEventHubs("event-hubs");
        var name = isNull ? null! : string.Empty;
        const string consumerGroupName = "group";
        var parent = new AzureEventHubResource("hub", "event-hub", resource.Resource);

        var action = () => new AzureEventHubConsumerGroupResource(name, consumerGroupName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureEventHubConsumerGroupResourceShouldThrowWhenConsumerGroupNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddAzureEventHubs("event-hubs");
        const string name = "consumer";
        var consumerGroupName = isNull ? null! : string.Empty;
        var parent = new AzureEventHubResource("hub", "event-hub", resource.Resource);

        var action = () => new AzureEventHubConsumerGroupResource(name, consumerGroupName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(consumerGroupName), exception.ParamName);
    }

    [Fact]
    public void CtorAzureEventHubConsumerGroupResourceShouldThrowWhenParentIsNullOrEmpty()
    {
        const string name = "consumer";
        const string consumerGroupName = "group";
        AzureEventHubResource parent = null!;

        var action = () => new AzureEventHubConsumerGroupResource(name, consumerGroupName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureEventHubResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string hubName = "event-hub";
        var parent = new AzureEventHubsResource("hub", (configureInfrastructure) => { });

        var action = () => new AzureEventHubResource(name, hubName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureEventHubResourceShouldThrowWhenHubNameIsNullOrEmpty(bool isNull)
    {
        const string name = "hub";
        var hubName = isNull ? null! : string.Empty;
        var parent = new AzureEventHubsResource("event-hubs", (configureInfrastructure) => { });

        var action = () => new AzureEventHubResource(name, hubName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(hubName), exception.ParamName);
    }

    [Fact]
    public void CtorAzureEventHubResourceShouldThrowWhenParentIsNullOrEmpty()
    {
        const string name = "hub";
        const string hubName = "event-hub";
        AzureEventHubsResource parent = null!;

        var action = () => new AzureEventHubResource(name, hubName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Fact]
    public void CtorAzureEventHubsEmulatorResourceShouldThrowWhenInnerResourceIsNullOrEmpty()
    {
        AzureEventHubsResource innerResource = null!;

        var action = () => new AzureEventHubsEmulatorResource(innerResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(innerResource), exception.ParamName);
    }

    [Fact]
    public void AddAzureEventHubsShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "event-hubs";

        var action = () => builder.AddAzureEventHubs(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureEventHubsShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureEventHubs(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddHub instead to add an Azure Event Hub.")]
    public void AddEventHubShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsResource> builder = null!;
        const string name = "event-hubs";

        var action = () => builder.AddEventHub(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddHub instead to add an Azure Event Hub.")]
    public void AddEventHubShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddEventHub(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddHubShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsResource> builder = null!;
        const string name = "event-hubs";

        var action = () => builder.AddHub(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddHubShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddHub(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubResource> builder = null!;
        Action<AzureEventHubResource> configure = (c) => { };

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPropertiesShouldThrowWhenConfigureIsNullOrEmpty()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs").AddHub("hub");
        Action<AzureEventHubResource> configure = null!;

        var action = () => builder.WithProperties(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void AddConsumerGroupShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubResource> builder = null!;
        const string name = "consumer";

        var action = () => builder.AddConsumerGroup(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddConsumerGroupShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs").AddHub("hub");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddConsumerGroup(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void RunAsEmulatorShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsResource> builder = null!;

        var action = () => builder.RunAsEmulator();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithDataBindMount();
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithDataVolume();
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    [Obsolete("Use WithHostPort instead.")]
    public void WithGatewayPortShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;
        int? port = null;

        var action = () => builder.WithGatewayPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithHostPortShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;
        int? port = null;

        var action = () => builder.WithHostPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationFileShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;
        const string path = "/Eventhubs_Emulator/ConfigFiles/Config.json";

        var action = () => builder.WithConfigurationFile(path);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithConfigurationFileShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs");

        var path = isNull ? null! : string.Empty;

        var action = () => builder.RunAsEmulator(configure => configure.WithConfigurationFile(path));

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(path), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationShouldThrowWhenBuilderIsNullOrEmpty()
    {
        IResourceBuilder<AzureEventHubsEmulatorResource> builder = null!;
        Action<JsonNode> configJson = (_) => { };

        var action = () => builder.WithConfiguration(configJson);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationShouldThrowWhenConfigJsonIsNullOrEmpty()
    {
        using var hostBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = hostBuilder.AddAzureEventHubs("event-hubs");
        Action<JsonNode> configJson = null!;

        var action = () => builder.RunAsEmulator(configure => configure.WithConfiguration(configJson));

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configJson), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureEventHubsResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureEventHubsResource(name, configureInfrastructure);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureEventHubsResourceShouldThrowWhenConfigureInfrastructureIsNullOrEmpty()
    {
        const string name = "hub";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureEventHubsResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
