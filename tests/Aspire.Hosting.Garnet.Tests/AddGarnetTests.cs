// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Garnet.Tests;

public class AddGarnetTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddGarnetContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddGarnet("myGarnet").PublishAsContainer();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<GarnetResource>());
        Assert.Equal("myGarnet", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(6379, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(GarnetContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(GarnetContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(GarnetContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public void AddGarnetContainerAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddGarnet("myGarnet", port: 8813);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<GarnetResource>());
        Assert.Equal("myGarnet", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(6379, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(8813, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(GarnetContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(GarnetContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(GarnetContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task GarnetCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddGarnet("myGarnet")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        await using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);
        Assert.Equal("{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={myGarnet-password.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith("localhost:2000", connectionString);
    }

    [Fact]
    public async Task VerifyWithoutPasswordManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet");

        var manifest = await ManifestUtils.GetManifest(garnet.Resource);

        var expectedManifest = $$"""
                                 {
                                   "type": "container.v0",
                                   "connectionString": "{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={myGarnet-password.value}",
                                   "image": "{{GarnetContainerImageTags.Registry}}/{{GarnetContainerImageTags.Image}}:{{GarnetContainerImageTags.Tag}}",
                                   "entrypoint": "/bin/sh",
                                   "args": [
                                     "-c",
                                     "/app/GarnetServer --protected-mode no --auth Password --password $GARNET_PASSWORD"
                                   ],
                                   "env": {
                                     "GARNET_PASSWORD": "{myGarnet-password.value}"
                                   },
                                   "bindings": {
                                     "tcp": {
                                       "scheme": "tcp",
                                       "protocol": "tcp",
                                       "transport": "tcp",
                                       "targetPort": 6379
                                     }
                                   }
                                 }
                                 """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyWithPasswordManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var password = "p@ssw0rd1";
        builder.Configuration["Parameters:pass"] = password;

        var pass = builder.AddParameter("pass");

        var garnet = builder.AddGarnet("myGarnet", password: pass);

        var manifest = await ManifestUtils.GetManifest(garnet.Resource);

        var expectedManifest = $$"""
                                 {
                                   "type": "container.v0",
                                   "connectionString": "{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={pass.value}",
                                   "image": "{{GarnetContainerImageTags.Registry}}/{{GarnetContainerImageTags.Image}}:{{GarnetContainerImageTags.Tag}}",
                                   "entrypoint": "/bin/sh",
                                   "args": [
                                     "-c",
                                     "/app/GarnetServer --protected-mode no --auth Password --password $GARNET_PASSWORD"
                                   ],
                                   "env": {
                                     "GARNET_PASSWORD": "{pass.value}"
                                   },
                                   "bindings": {
                                     "tcp": {
                                       "scheme": "tcp",
                                       "protocol": "tcp",
                                       "transport": "tcp",
                                       "targetPort": 6379
                                     }
                                   }
                                 }
                                 """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithPersistence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var garnet = builder.AddGarnet("myGarnet")
            .WithPersistence();

        var manifest = await ManifestUtils.GetManifest(garnet.Resource);

        var expectedManifest = $$"""
                                 {
                                   "type": "container.v0",
                                   "connectionString": "{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={myGarnet-password.value}",
                                   "image": "{{GarnetContainerImageTags.Registry}}/{{GarnetContainerImageTags.Image}}:{{GarnetContainerImageTags.Tag}}",
                                   "entrypoint": "/bin/sh",
                                   "args": [
                                     "-c",
                                     "/app/GarnetServer --protected-mode no --auth Password --password $GARNET_PASSWORD --checkpointdir /data/checkpoints --recover --aof --aof-commit-freq 60000"
                                   ],
                                   "env": {
                                     "GARNET_PASSWORD": "{myGarnet-password.value}"
                                   },
                                   "bindings": {
                                     "tcp": {
                                       "scheme": "tcp",
                                       "protocol": "tcp",
                                       "transport": "tcp",
                                       "targetPort": 6379
                                     }
                                   }
                                 }
                                 """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataVolumeAddsVolumeAnnotation(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet");
        if (isReadOnly.HasValue)
        {
            garnet.WithDataVolume(isReadOnly: isReadOnly.Value);
        }
        else
        {
            garnet.WithDataVolume();
        }

        var volumeAnnotation = garnet.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal($"{builder.GetVolumePrefix()}-myGarnet-data", volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountAddsMountAnnotation(bool? isReadOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet");
        if (isReadOnly.HasValue)
        {
            garnet.WithDataBindMount("mygarnetdata", isReadOnly: isReadOnly.Value);
        }
        else
        {
            garnet.WithDataBindMount("mygarnetdata");
        }

        var volumeAnnotation = garnet.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mygarnetdata"), volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public async Task WithDataVolumeAddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                              .WithDataVolume();

        Assert.True(garnet.Resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbacks));

        var args = await GetCommandLineArgs(garnet);
        Assert.Contains("--checkpointdir /data/checkpoints --recover --aof --aof-commit-freq 60000", args);
    }

    [Fact]
    public async Task WithDataVolumeDoesNotAddPersistenceAnnotationIfIsReadOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                           .WithDataVolume(isReadOnly: true);

        var args = await GetCommandLineArgs(garnet);
        Assert.DoesNotContain("--checkpointdir", args);
    }

    [Fact]
    public async Task WithDataBindMountAddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                           .WithDataBindMount("mygarnetdata");

        Assert.True(garnet.Resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbacks));

        var args = await GetCommandLineArgs(garnet);
        Assert.Contains("--checkpointdir /data/checkpoints --recover --aof --aof-commit-freq 60000", args);
    }

    private static async Task<string> GetCommandLineArgs(IResourceBuilder<GarnetResource> builder)
    {
        var args = await ArgumentEvaluator.GetArgumentListAsync(builder.Resource);
        return string.Join(" ", args);
    }

    [Fact]
    public async Task WithDataBindMountDoesNotAddPersistenceAnnotationIfIsReadOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                           .WithDataBindMount("mygarnetdata", isReadOnly: true);

        var args = await GetCommandLineArgs(garnet);
        Assert.DoesNotContain("--checkpointdir", args);
    }

    [Fact]
    public async Task WithPersistenceReplacesPreviousAnnotationInstances()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                           .WithDataVolume()
                           .WithPersistence(TimeSpan.FromSeconds(10));

        Assert.True(garnet.Resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbacks));

        var args = await GetCommandLineArgs(garnet);
        Assert.Contains("--checkpointdir /data/checkpoints --recover --aof --aof-commit-freq 10000", args);

        // ensure `--checkpointdir` is not added twice
        var saveIndex = args.IndexOf("--checkpointdir");
        Assert.DoesNotContain("--checkpointdir", args.Substring(saveIndex + 1));
    }

    [Fact]
    public void WithPersistenceAddsCommandLineArgsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var garnet = builder.AddGarnet("myGarnet")
                           .WithPersistence(TimeSpan.FromSeconds(60));

        Assert.True(garnet.Resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsAnnotations));
        Assert.NotNull(argsAnnotations.SingleOrDefault());
    }

    [Fact]
    public async Task AddGarnetContainerWithPasswordAnnotationMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var password = "p@ssw0rd1";
        var pass = builder.AddParameter("pass", password);
        var garnet = builder.
            AddGarnet("myGarnet", password: pass)
           .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5001));

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<GarnetResource>());

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync(default);
        Assert.Equal("{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={pass.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith($"localhost:5001,password={password}", connectionString);
    }

    [Fact]
    public void GarnetCreatesConnectionStringWithPassword()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var password = "p@ssw0rd1";
        var pass = appBuilder.AddParameter("pass", password);
        appBuilder.AddGarnet("myGarnet", password: pass);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        Assert.Equal("{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={pass.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void GarnetCreatesConnectionStringWithPasswordAndPort()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var password = "p@ssw0rd1";
        var pass = appBuilder.AddParameter("pass", password);
        appBuilder.AddGarnet("myGarnet", port: 3000, password: pass);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        Assert.Equal("{myGarnet.bindings.tcp.host}:{myGarnet.bindings.tcp.port},password={pass.value}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }
}
