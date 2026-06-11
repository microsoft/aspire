// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class AddRabbitMQTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddRabbitMQAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rmq = appBuilder.AddRabbitMQ("rmq");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", rmq.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddRabbitMQDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var rmq = appBuilder.AddRabbitMQ("rmq");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", rmq.Resource.PasswordParameter.Default?.GetType().FullName);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 15672)]
    public void AddRabbitMQContainerWithDefaultsAddsAnnotationMetadata(bool withManagementPlugin, int? withManagementPluginPort)
    {
        var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitmq = appBuilder.AddRabbitMQ("rabbit");

        if (withManagementPlugin)
        {
            rabbitmq.WithManagementPlugin(withManagementPluginPort);
        }

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<RabbitMQServerResource>());
        Assert.Equal("rabbit", containerResource.Name);

        var primaryEndpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "tcp");
        Assert.Equal(5672, primaryEndpoint.TargetPort);
        Assert.False(primaryEndpoint.IsExternal);
        Assert.Equal("tcp", primaryEndpoint.Name);
        Assert.Null(primaryEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, primaryEndpoint.Protocol);
        Assert.Equal("tcp", primaryEndpoint.Transport);
        Assert.Equal("tcp", primaryEndpoint.UriScheme);

        if (withManagementPlugin)
        {
            var mangementEndpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "management");
            Assert.Equal(15672, mangementEndpoint.TargetPort);
            Assert.False(primaryEndpoint.IsExternal);
            Assert.Equal("management", mangementEndpoint.Name);
            Assert.Equal(ProtocolType.Tcp, mangementEndpoint.Protocol);
            Assert.Equal("http", mangementEndpoint.Transport);
            Assert.Equal("http", mangementEndpoint.UriScheme);

            if (!withManagementPluginPort.HasValue)
            {
                Assert.Null(mangementEndpoint.Port);
            }
            else
            {
                Assert.Equal(withManagementPluginPort.Value, mangementEndpoint.Port);
            }
        }

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(RabbitMQContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(withManagementPlugin ? RabbitMQContainerImageTags.ManagementTag : RabbitMQContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(RabbitMQContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task RabbitMQCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "p@ssw0rd1");
        appBuilder
            .AddRabbitMQ("rabbit", password: pass)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27011));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var rabbitMqResource = Assert.Single(appModel.Resources.OfType<RabbitMQServerResource>());
        var connectionStringResource = rabbitMqResource as IResourceWithConnectionString;
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);

        Assert.Equal("amqp://guest:p%40ssw0rd1@localhost:27011", connectionString);
        Assert.Equal("amqp://guest:{pass.value}@{rabbit.bindings.tcp.host}:{rabbit.bindings.tcp.port}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Theory]
    [InlineData(null, RabbitMQContainerImageTags.ManagementTag)]
    [InlineData("management", "management")]
    [InlineData("4.0.9-management", "4.0.9-management")]
    [InlineData("alpine", "management-alpine")]
    [InlineData("management-alpine", "management-alpine")]
    [InlineData("4.0.9-management-alpine", "4.0.9-management-alpine")]
    [InlineData("3", "3-management")]
    [InlineData("3.12", "3.12-management")]
    [InlineData("3.12.0", "3.12.0-management")]
    [InlineData("3-alpine", "3-management-alpine")]
    [InlineData("3.12-alpine", "3.12-management-alpine")]
    [InlineData("3.12.0-alpine", "3.12.0-management-alpine")]
    [InlineData("999", "999-management")]
    [InlineData("12345", "12345-management")]
    [InlineData("12345.00.12", "12345.00.12-management")]
    public void WithManagementPluginUpdatesContainerImageTagToEnableManagementPlugin(string? imageTag, string expectedTag)
    {
        var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitmq = appBuilder.AddRabbitMQ("rabbit");
        if (imageTag is not null)
        {
            rabbitmq.WithImageTag(imageTag);
        }
        rabbitmq.WithManagementPlugin();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<RabbitMQServerResource>());
        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(expectedTag, containerAnnotation.Tag);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("test")]
    [InlineData(".123")]
    [InlineData(".")]
    [InlineData(".1.2")]
    [InlineData("1.2.")]
    [InlineData("1.٩.3")]
    [InlineData("1.2..3")]
    [InlineData("not-supported")]
    public void WithManagementPluginThrowsForUnsupportedContainerImageTag(string imageTag)
    {
        using var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitmq = appBuilder.AddRabbitMQ("rabbit");
        rabbitmq.WithImageTag(imageTag);

        Assert.Throws<DistributedApplicationException>(rabbitmq.WithManagementPlugin);
    }

    [Theory]
    [InlineData("notrabbitmq")]
    [InlineData("not-supported")]
    public void WithManagementPluginThrowsForUnsupportedContainerImageName(string imageName)
    {
        using var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitmq = appBuilder.AddRabbitMQ("rabbit");
        rabbitmq.WithImage(imageName);

        Assert.Throws<DistributedApplicationException>(rabbitmq.WithManagementPlugin);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("custom.url")]
    [InlineData("not.the.default")]
    public void WithManagementPluginThrowsForUnsupportedContainerImageRegistry(string registry)
    {
        using var appBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitmq = appBuilder.AddRabbitMQ("rabbit");
        rabbitmq.WithImageRegistry(registry);

        Assert.Throws<DistributedApplicationException>(rabbitmq.WithManagementPlugin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyManifest(bool withManagementPlugin)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var rabbit = builder.AddRabbitMQ("rabbit");
        if (withManagementPlugin)
        {
            rabbit.WithManagementPlugin();
        }
        var manifest = await ManifestUtils.GetManifest(rabbit.Resource);

        var expectedTag = withManagementPlugin ? RabbitMQContainerImageTags.ManagementTag : RabbitMQContainerImageTags.Tag;
        var managementBinding = withManagementPlugin
            ? """
            ,
                "management": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 15672
                }
            """
            : "";
        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "amqp://guest:{rabbit-password-uri-encoded.value}@{rabbit.bindings.tcp.host}:{rabbit.bindings.tcp.port}",
              "image": "{{RabbitMQContainerImageTags.Registry}}/{{RabbitMQContainerImageTags.Image}}:{{expectedTag}}",
              "env": {
                "RABBITMQ_DEFAULT_USER": "guest",
                "RABBITMQ_DEFAULT_PASS": "{rabbit-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5672
                }{{managementBinding}}
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var userNameParameter = builder.AddParameter("user");
        var passwordParameter = builder.AddParameter("pass");

        var rabbit = builder.AddRabbitMQ("rabbit", userNameParameter, passwordParameter);
        var manifest = await ManifestUtils.GetManifest(rabbit.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "amqp://{user-uri-encoded.value}:{pass-uri-encoded.value}@{rabbit.bindings.tcp.host}:{rabbit.bindings.tcp.port}",
              "image": "{{RabbitMQContainerImageTags.Registry}}/{{RabbitMQContainerImageTags.Image}}:{{RabbitMQContainerImageTags.Tag}}",
              "env": {
                "RABBITMQ_DEFAULT_USER": "{user.value}",
                "RABBITMQ_DEFAULT_PASS": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5672
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        rabbit = builder.AddRabbitMQ("rabbit2", userNameParameter);
        manifest = await ManifestUtils.GetManifest(rabbit.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "amqp://{user-uri-encoded.value}:{rabbit2-password-uri-encoded.value}@{rabbit2.bindings.tcp.host}:{rabbit2.bindings.tcp.port}",
              "image": "{{RabbitMQContainerImageTags.Registry}}/{{RabbitMQContainerImageTags.Image}}:{{RabbitMQContainerImageTags.Tag}}",
              "env": {
                "RABBITMQ_DEFAULT_USER": "{user.value}",
                "RABBITMQ_DEFAULT_PASS": "{rabbit2-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5672
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        rabbit = builder.AddRabbitMQ("rabbit3", password: passwordParameter);
        manifest = await ManifestUtils.GetManifest(rabbit.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "amqp://guest:{pass-uri-encoded.value}@{rabbit3.bindings.tcp.host}:{rabbit3.bindings.tcp.port}",
              "image": "{{RabbitMQContainerImageTags.Registry}}/{{RabbitMQContainerImageTags.Image}}:{{RabbitMQContainerImageTags.Tag}}",
              "env": {
                "RABBITMQ_DEFAULT_USER": "guest",
                "RABBITMQ_DEFAULT_PASS": "{pass.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 5672
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }
}
