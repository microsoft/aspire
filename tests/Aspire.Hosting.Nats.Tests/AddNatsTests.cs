// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Nats.Tests;

public class AddNatsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddNatsAddsGeneratedPasswordParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var nats = appBuilder.AddNats("nats");
        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", nats.Resource.PasswordParameter!.Default?.GetType().FullName);
    }

    [Fact]
    public void AddNatsDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nats = appBuilder.AddNats("nats");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", nats.Resource.PasswordParameter!.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddNatsSetsDefaultUserNameAndPasswordAndIncludesThemInConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var nats = appBuilder.AddNats("nats")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 4222));

        Assert.NotNull(nats.Resource.PasswordParameter);
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.False(string.IsNullOrEmpty(nats.Resource.PasswordParameter!.Value));
#pragma warning restore CS0618 // Type or member is obsolete

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var natsResource = Assert.Single(appModel.Resources.OfType<NatsServerResource>());
        var connectionStringResource = natsResource as IResourceWithConnectionString;
        Assert.NotNull(connectionStringResource);
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"nats://nats:{natsResource.PasswordParameter?.Value}@localhost:4222", connectionString);
#pragma warning restore CS0618 // Type or member is obsolete
        Assert.Equal("nats://nats:{nats-password.value}@{nats.bindings.tcp.host}:{nats.bindings.tcp.port}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task AddNatsSetsUserNameAndPasswordAndIncludesThemInConnection()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var userParameters = appBuilder.AddParameter("user", "usr");
        var passwordParameters = appBuilder.AddParameter("pass", "password");

        var nats = appBuilder.AddNats("nats", userName: userParameters, password: passwordParameters)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 4222));

        Assert.NotNull(nats.Resource.UserNameParameter);
        Assert.NotNull(nats.Resource.PasswordParameter);

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("usr", nats.Resource.UserNameParameter!.Value);
        Assert.Equal("password", nats.Resource.PasswordParameter!.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<NatsServerResource>()) as IResourceWithConnectionString;
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

        Assert.Equal("nats://usr:password@localhost:4222", connectionString);
        Assert.Equal("nats://{user.value}:{pass.value}@{nats.bindings.tcp.host}:{nats.bindings.tcp.port}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task AddNatsContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddNats("nats");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<NatsServerResource>());
        Assert.Equal("nats", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(4222, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(NatsContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(NatsContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(NatsContainerImageTags.Registry, containerAnnotation.Registry);

        var args = await ArgumentEvaluator.GetArgumentListAsync(containerResource);

        Assert.Collection(args,
            arg => Assert.Equal("--user", arg),
            arg => Assert.Equal("nats", arg),
            arg => Assert.Equal("--pass", arg),
            arg => Assert.False(string.IsNullOrEmpty(arg))
        );
    }

    [Fact]
    public async Task AddNatsContainerAddsAnnotationMetadata()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\tmp\dev-data" : "/tmp/dev-data";

        var appBuilder = DistributedApplication.CreateBuilder();
        var user = appBuilder.AddParameter("user", "usr");
        var pass = appBuilder.AddParameter("pass", "pass");

        appBuilder.AddNats("nats", 1234, user, pass).WithJetStream().WithDataBindMount(path);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<NatsServerResource>());
        Assert.Equal("nats", containerResource.Name);

        var mountAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(path, mountAnnotation.Source);
        Assert.Equal("/var/lib/nats", mountAnnotation.Target);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(4222, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(1234, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(NatsContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(NatsContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(NatsContainerImageTags.Registry, containerAnnotation.Registry);

        var args = await ArgumentEvaluator.GetArgumentListAsync(containerResource);

        Assert.Collection(args,
            arg => Assert.Equal("--user", arg),
            arg => Assert.Equal("usr", arg),
            arg => Assert.Equal("--pass", arg),
            arg => Assert.Equal("pass", arg),
            arg => Assert.Equal("-js", arg),
            arg => Assert.Equal("-sd", arg),
            arg => Assert.Equal("/var/lib/nats", arg)
        );
    }

    [Fact]
    public void WithNatsContainerOnMultipleResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddNats("nats1");
        builder.AddNats("nats2");

        Assert.Equal(2, builder.Resources.OfType<NatsServerResource>().Count());
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var nats = builder.AddNats("nats");

        var manifest = await ManifestUtils.GetManifest(nats.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "nats://nats:{nats-password-uri-encoded.value}@{nats.bindings.tcp.host}:{nats.bindings.tcp.port}",
              "image": "{{NatsContainerImageTags.Registry}}/{{NatsContainerImageTags.Image}}:{{NatsContainerImageTags.Tag}}",
              "args": [
                "--user",
                "nats",
                "--pass",
                "{nats-password.value}"
              ],
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 4222
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWihtParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var userNameParameter = builder.AddParameter("user");
        var passwordParameter = builder.AddParameter("pass");

        var nats = builder.AddNats("nats", userName: userNameParameter, password: passwordParameter)
            .WithJetStream();

        var manifest = await ManifestUtils.GetManifest(nats.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "nats://{user-uri-encoded.value}:{pass-uri-encoded.value}@{nats.bindings.tcp.host}:{nats.bindings.tcp.port}",
              "image": "{{NatsContainerImageTags.Registry}}/{{NatsContainerImageTags.Image}}:{{NatsContainerImageTags.Tag}}",
              "args": [
                "--user",
                "{user.value}",
                "--pass",
                "{pass.value}",
                "-js"
              ],
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 4222
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        nats = builder.AddNats("nats2", userName: userNameParameter);

        manifest = await ManifestUtils.GetManifest(nats.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "nats://{user-uri-encoded.value}:{nats2-password-uri-encoded.value}@{nats2.bindings.tcp.host}:{nats2.bindings.tcp.port}",
              "image": "{{NatsContainerImageTags.Registry}}/{{NatsContainerImageTags.Image}}:{{NatsContainerImageTags.Tag}}",
              "args": [
                "--user",
                "{user.value}",
                "--pass",
                "{nats2-password.value}"
              ],
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 4222
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        nats = builder.AddNats("nats3", password: passwordParameter);

        manifest = await ManifestUtils.GetManifest(nats.Resource);

        expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "nats://nats:{pass-uri-encoded.value}@{nats3.bindings.tcp.host}:{nats3.bindings.tcp.port}",
              "image": "{{NatsContainerImageTags.Registry}}/{{NatsContainerImageTags.Image}}:{{NatsContainerImageTags.Tag}}",
              "args": [
                "--user",
                "nats",
                "--pass",
                "{pass.value}"
              ],
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 4222
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }
}
