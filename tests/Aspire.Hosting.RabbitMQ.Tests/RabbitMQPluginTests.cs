// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQPluginTests
{
    [Fact]
    public void WithPlugin_Enum_AddsAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.WithPlugin(RabbitMQPlugin.Prometheus);

        var annotations = server.Resource.Annotations.OfType<RabbitMQPluginAnnotation>();
        Assert.Contains(annotations, a => a.PluginName == "rabbitmq_prometheus");
    }

    [Fact]
    public void WithPlugin_String_AddsAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.WithPlugin("my_custom_plugin");

        var annotations = server.Resource.Annotations.OfType<RabbitMQPluginAnnotation>();
        Assert.Contains(annotations, a => a.PluginName == "my_custom_plugin");
    }

    [Fact]
    public void WithPlugin_String_ThrowsOnNullOrWhiteSpace()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        Assert.Throws<ArgumentException>(() => server.WithPlugin(""));
        Assert.Throws<ArgumentException>(() => server.WithPlugin(" "));
        Assert.Throws<ArgumentNullException>(() => server.WithPlugin((string)null!));
    }

    [Fact]
    public async Task WithPlugin_GeneratesEnabledPluginsFile_ContainingOnlyRequestedPlugins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        // Use WithPlugin directly (not AddShovel, which transitively enables management)
        server.WithPlugin(RabbitMQPlugin.Shovel);
        server.WithPlugin("my_custom_plugin");

        var containerFiles = server.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>();
        var annotation = Assert.Single(containerFiles);
        Assert.Equal("/etc/rabbitmq", annotation.DestinationPath);

        var context = new ContainerFileSystemCallbackContext
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
            Model = server.Resource
        };
        var items = await annotation.Callback(context, default);

        var file = Assert.Single(items) as ContainerFile;
        Assert.NotNull(file);
        Assert.Equal("enabled_plugins", file.Name);

        var content = file.Contents;
        Assert.NotNull(content);

        // Should contain exactly the requested plugins — no hard-coded extras
        Assert.Contains("rabbitmq_shovel", content);
        Assert.Contains("my_custom_plugin", content);
        Assert.DoesNotContain("rabbitmq_management_agent", content);
        Assert.DoesNotContain("rabbitmq_web_dispatch", content);
        Assert.DoesNotContain("rabbitmq_prometheus", content);

        // Should be formatted as an Erlang list
        Assert.StartsWith("[", content);
        Assert.EndsWith("].", content);
    }

    [Fact]
    public async Task WithManagementPlugin_ThenWithPlugin_IncludesManagementPluginsInFile()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit").WithManagementPlugin();

        server.WithPlugin("my_custom_plugin");

        var containerFiles = server.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>();
        var annotation = Assert.Single(containerFiles);

        var context = new ContainerFileSystemCallbackContext
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
            Model = server.Resource
        };
        var items = await annotation.Callback(context, default);

        var file = Assert.Single(items) as ContainerFile;
        Assert.NotNull(file);
        var content = file.Contents;
        Assert.NotNull(content);

        // Management plugin annotations are added explicitly by WithManagementPlugin
        Assert.Contains("rabbitmq_management", content);
        Assert.Contains("rabbitmq_management_agent", content);
        Assert.Contains("rabbitmq_web_dispatch", content);
        Assert.Contains("rabbitmq_prometheus", content);
        Assert.Contains("my_custom_plugin", content);
    }

    [Fact]
    public async Task WithPlugin_DeduplicatesPlugins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.WithPlugin(RabbitMQPlugin.Prometheus);
        server.WithPlugin("rabbitmq_prometheus");

        var containerFiles = server.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>();
        var annotation = Assert.Single(containerFiles);

        var context = new ContainerFileSystemCallbackContext
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
            Model = server.Resource
        };
        var items = await annotation.Callback(context, default);

        var file = Assert.Single(items) as ContainerFile;
        Assert.NotNull(file);
        var content = file.Contents;

        // Should only appear once
        var count = content!.Split("rabbitmq_prometheus").Length - 1;
        Assert.Equal(1, count);
    }
}
