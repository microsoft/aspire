// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Seq.Tests;

public class AddSeqTests
{
    [Fact]
    public void AddSeqContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddSeq("mySeq").PublishAsContainer();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SeqResource>());
        Assert.Equal("mySeq", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(80, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SeqContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SeqContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SeqContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public void AddSeqContainerAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddSeq("mySeq", port: 9813);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SeqResource>());
        Assert.Equal("mySeq", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(80, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(9813, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SeqContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SeqContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SeqContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task SeqCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddSeq("mySeq")
            .WithEndpoint("http", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal("{mySeq.bindings.http.url}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith("http://localhost:2000", connectionString);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var seq = builder.AddSeq("seq");

        var manifest = await ManifestUtils.GetManifest(seq.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "{seq.bindings.http.url}",
              "image": "{{SeqContainerImageTags.Registry}}/{{SeqContainerImageTags.Image}}:{{SeqContainerImageTags.Tag}}",
              "env": {
                "ACCEPT_EULA": "Y",
                "SEQ_FIRSTRUN_NOAUTHENTICATION": "True"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
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
        using var builder = TestDistributedApplicationBuilder.Create();
        var seq = builder.AddSeq("mySeq");
        if (isReadOnly.HasValue)
        {
            seq.WithDataVolume(isReadOnly: isReadOnly.Value);
        }
        else
        {
            seq.WithDataVolume();
        }

        var volumeAnnotation = seq.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal($"{builder.GetVolumePrefix()}-mySeq-data", volumeAnnotation.Source);
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
        using var builder = TestDistributedApplicationBuilder.Create();
        var seq = builder.AddSeq("mySeq");
        if (isReadOnly.HasValue)
        {
            seq.WithDataBindMount("mydata", isReadOnly: isReadOnly.Value);
        }
        else
        {
            seq.WithDataBindMount("mydata");
        }

        var volumeAnnotation = seq.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mydata"), volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.Equal(isReadOnly ?? false, volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void AddSeqContainerWithAdminPasswordAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var password = appBuilder.AddParameter("password", secret: true);
        appBuilder.AddSeq("mySeq", password).PublishAsContainer();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SeqResource>());
        Assert.Equal("mySeq", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(80, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SeqContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SeqContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SeqContainerImageTags.Registry, containerAnnotation.Registry);

        // Verify environment annotations exist (they will be checked in manifest tests)
        var envAnnotations = containerResource.Annotations.OfType<EnvironmentCallbackAnnotation>();
        Assert.NotEmpty(envAnnotations);
    }

    [Fact]
    public void AddSeqContainerWithAdminPasswordAndPortAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var password = appBuilder.AddParameter("password", secret: true);
        appBuilder.AddSeq("mySeq", password, port: 9813);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<SeqResource>());
        Assert.Equal("mySeq", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(80, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(9813, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(SeqContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(SeqContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(SeqContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task SeqWithAdminPasswordCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var password = appBuilder.AddParameter("password", secret: true);
        appBuilder.AddSeq("mySeq", password)
            .WithEndpoint("http", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<IResourceWithConnectionString>());
        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal("{mySeq.bindings.http.url}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.StartsWith("http://localhost:2000", connectionString);
    }

    [Fact]
    public async Task VerifyManifestWithAdminPassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", secret: true);
        var seq = builder.AddSeq("seq", password);

        var manifest = await ManifestUtils.GetManifest(seq.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "{seq.bindings.http.url}",
              "image": "{{SeqContainerImageTags.Registry}}/{{SeqContainerImageTags.Image}}:{{SeqContainerImageTags.Tag}}",
              "env": {
                "ACCEPT_EULA": "Y",
                "SEQ_FIRSTRUN_ADMINPASSWORD": "{password.value}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithoutAdminPassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var seq = builder.AddSeq("seq", adminPassword: null);

        var manifest = await ManifestUtils.GetManifest(seq.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "{seq.bindings.http.url}",
              "image": "{{SeqContainerImageTags.Registry}}/{{SeqContainerImageTags.Image}}:{{SeqContainerImageTags.Tag}}",
              "env": {
                "ACCEPT_EULA": "Y",
                "SEQ_FIRSTRUN_NOAUTHENTICATION": "True"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }
}
