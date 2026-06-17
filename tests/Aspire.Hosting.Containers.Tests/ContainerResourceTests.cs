// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Containers.Tests;

public class ContainerResourceTests
{
    [Fact]
    public void AddContainerAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddContainer("container", "none");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("container", containerResource.Name);
        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("latest", containerAnnotation.Tag);
        Assert.Equal("none", containerAnnotation.Image);
        Assert.Null(containerAnnotation.Registry);
    }

    [Fact]
    public void AddContainerAddsAnnotationMetadataWithTag()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddContainer("container", "none", "nightly");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("container", containerResource.Name);
        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("nightly", containerAnnotation.Tag);
        Assert.Equal("none", containerAnnotation.Image);
        Assert.Null(containerAnnotation.Registry);
    }

    [Fact]
    public void AddContainerWithTagInImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddContainer("container", "image:tag");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("container", containerResource.Name);
        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("tag", containerAnnotation.Tag);
        Assert.Equal("image", containerAnnotation.Image);
        Assert.Null(containerAnnotation.SHA256);
        Assert.Null(containerAnnotation.Registry);
    }

    [Fact]
    public void AddContainerWithSha256InImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddContainer("container", "imagewithdigest@sha256:01234567890abcdef01234567890abcdef01234567890abcdef01234567890ab");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var containerResource = Assert.Single(containerResources);
        Assert.Equal("container", containerResource.Name);
        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("01234567890abcdef01234567890abcdef01234567890abcdef01234567890ab", containerAnnotation.SHA256);
        Assert.Equal("imagewithdigest", containerAnnotation.Image);
        Assert.Null(containerAnnotation.Tag);
        Assert.Null(containerAnnotation.Registry);
    }

    [Fact]
    public async Task AddContainerWithArgs()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var testResource = new TestResource("test", "connectionString");

        var c1 = appBuilder.AddContainer("c1", "image2")
            .WithEndpoint("ep", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new(e, "localhost", 1234, targetPortExpression: "1234");

                // For container-container lookup we need to add an AllocatedEndpoint on the container network side
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "c1.dev.internal", 2234, EndpointBindingMode.SingleAddress, targetPortExpression: "2234", KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });

        var c2 = appBuilder.AddContainer("container", "none")
             .WithEndpoint("ep", e =>
             {
                 e.UriScheme = "http";
                 // We only care about the container-side endpoint for this test
                 e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "container.dev.internal", 5678, EndpointBindingMode.SingleAddress, targetPortExpression: "5678", KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
             })
             .WithArgs(context =>
             {
                 context.Args.Add("arg1");
                 context.Args.Add(c1.GetEndpoint("ep"));
                 context.Args.Add(testResource);
                 context.Args.Add(((IResourceWithEndpoints)context.Resource).GetEndpoint("ep"));
             });

        using var app = appBuilder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(c2.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("arg1", arg),
            arg => Assert.Equal("http://c1.dev.internal:2234", arg),
            arg => Assert.Equal("connectionString", arg),
            arg => Assert.Equal("http://container.dev.internal:5678", arg));

        // We don't yet process relationships set via the callbacks
        // so we don't see the testResource2 nor exe1
        Assert.False(c2.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));

        var manifest = await ManifestUtils.GetManifest(c2.Resource);

        var expectedManifest =
        """
        {
          "type": "container.v0",
          "image": "none:latest",
          "args": [
            "arg1",
            "{c1.bindings.ep.url}",
            "{test.connectionString}",
            "{container.bindings.ep.url}"
          ],
          "bindings": {
            "ep": {
              "scheme": "http",
              "protocol": "tcp",
              "transport": "http",
              "targetPort": 8000
            }
          }
        }
        """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task EnsureContainerWithEndpointsEmitsContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder(["--publisher", "manifest"]);

        builder.AddContainer("grafana", "grafana/grafana")
               .WithHttpEndpoint(3000);

        using var app = builder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        var manifest = await ManifestUtils.GetManifest(containerResource);

        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "grafana/grafana:latest",
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 3000
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task EnsureContainerWithCustomEntrypointEmitsEntrypoint()
    {
        var builder = DistributedApplication.CreateBuilder(["--publisher", "manifest"]);

        builder.AddContainer("grafana", "grafana/grafana")
               .WithEntrypoint("custom");

        // Build AppHost so that publisher can be resolved.
        using var app = builder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        var manifest = await ManifestUtils.GetManifest(containerResource);

        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "grafana/grafana:latest",
              "entrypoint": "custom"
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public void AddBindMountResolvesRelativePathsRelativeToTheAppHostDirectory()
    {
        var basePath = OperatingSystem.IsWindows() ? @"C:\root\volumes" : "/root/volumes";

        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { ProjectDirectory = basePath });

        appBuilder.AddContainer("container", "none")
            .WithBindMount("source", "/target");

        using var app = appBuilder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        Assert.True(containerResource.TryGetLastAnnotation<ContainerMountAnnotation>(out var mountAnnotation));

        Assert.Equal(Path.Combine(basePath, "source"), mountAnnotation.Source);
    }

    [Fact]
    public async Task EnsureContainerWithVolumesEmitsVolumes()
    {
        var builder = DistributedApplication.CreateBuilder(["--publisher", "manifest"]);

        builder.AddContainer("containerwithvolumes", "image/name")
            .WithVolume("myvolume", "/mount/here")
            .WithVolume("myreadonlyvolume", "/mount/there", isReadOnly: true)
            .WithVolume(null! /* anonymous volume */, "/mount/everywhere");

        using var app = builder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        var manifest = await ManifestUtils.GetManifest(containerResource);

        var expectedManifest = """
            {
              "type": "container.v0",
              "image": "image/name:latest",
              "volumes": [
                {
                  "name": "myvolume",
                  "target": "/mount/here",
                  "readOnly": false
                },
                {
                  "name": "myreadonlyvolume",
                  "target": "/mount/there",
                  "readOnly": true
                },
                {
                  "target": "/mount/everywhere",
                  "readOnly": false
                }
              ]
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task EnsureContainerWithBindMountsEmitsBindMounts()
    {
        var appHostPath = OperatingSystem.IsWindows() ? @"C:\projects\apphost" : "/projects/apphost";

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostPath,
            Args = ["--publisher", "manifest"]
        });

        builder.AddContainer("containerwithbindmounts", "image/name")
            .WithBindMount("./some/source", "/bound")
            .WithBindMount("not/relative/qualified", "/another/place")
            .WithBindMount(".\\some\\other\\source", "\\mount\\here")
            .WithBindMount("./some/file/path.txt", "/mount/there.txt", isReadOnly: true);

        using var app = builder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        var manifest = await ManifestUtils.GetManifest(containerResource, manifestDirectory: appHostPath);

        var expectedManifest = """
            {
              "type": "container.v0",
              "image": "image/name:latest",
              "bindMounts": [
                {
                  "source": "some/source",
                  "target": "/bound",
                  "readOnly": false
                },
                {
                  "source": "not/relative/qualified",
                  "target": "/another/place",
                  "readOnly": false
                },
                {
                  "source": "some/other/source",
                  "target": "/mount/here",
                  "readOnly": false
                },
                {
                  "source": "some/file/path.txt",
                  "target": "/mount/there.txt",
                  "readOnly": true
                }
              ]
            }
            """;

        Assert.Equal("containerwithbindmounts", containerResource.Name);
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public void WithBindMountHandlesDockerSocketPath()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddContainer("container", "none")
            .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock");

        using var app = appBuilder.Build();

        var containerResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().GetContainerResources());

        Assert.True(containerResource.TryGetLastAnnotation<ContainerMountAnnotation>(out var mountAnnotation));

        Assert.Equal("/var/run/docker.sock", mountAnnotation.Source);
        Assert.Equal("/var/run/docker.sock", mountAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, mountAnnotation.Type);
        Assert.False(mountAnnotation.IsReadOnly);
    }

    private sealed class TestResource(string name, string connectionString) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{connectionString}");
    }
}
