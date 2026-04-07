// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Milvus.Tests;
public class AddMilvusTests(ITestOutputHelper testOutputHelper)
{
    private const int MilvusPortGrpc = 19530;

    [Fact]
    public void AddMilvusWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("apikey", "pass");
        appBuilder.AddMilvus("my-milvus", apiKey: pass);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("my-milvus", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MilvusContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MilvusContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MilvusContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = containerResource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == "grpc");
        Assert.NotNull(endpoint);
        Assert.Equal(MilvusPortGrpc, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("grpc", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
    }

    [Fact]
    public void AddMilvusAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("apikey", "pass");
        appBuilder.AddMilvus("my-milvus", apiKey: pass);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("my-milvus", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(MilvusContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(MilvusContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(MilvusContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = containerResource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == "grpc");
        Assert.NotNull(endpoint);
        Assert.Equal(MilvusPortGrpc, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("grpc", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
    }

    [Fact]
    public async Task MilvusCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("apikey", "pass");

        var milvus = appBuilder.AddMilvus("my-milvus", pass)
                                 .WithEndpoint("grpc", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", MilvusPortGrpc));

        var connectionStringResource = milvus.Resource as IResourceWithConnectionString;

        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal($"Endpoint=http://localhost:19530;Key=root:pass", connectionString);
    }

    [Fact]
    public async Task MilvusClientAppWithReferenceContainsConnectionStrings()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("apikey", "pass");

        var milvus = appBuilder.AddMilvus("my-milvus", pass)
            .WithEndpoint("grpc", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", MilvusPortGrpc);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "my-milvus.dev.internal", MilvusPortGrpc, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });

        var projectA = appBuilder.AddProject<ProjectA>("projecta", o => o.ExcludeLaunchProfile = true)
            .WithReference(milvus);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);

        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__my-milvus" && kvp.Value == "Endpoint=http://localhost:19530;Key=root:pass");

        var container1 = appBuilder.AddContainer("container1", "fake")
            .WithReference(milvus);

        // Call environment variable callbacks.
        var containerConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container1.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var containerServicesKeysCount = containerConfig.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, containerServicesKeysCount);

        Assert.Contains(containerConfig, kvp => kvp.Key == "ConnectionStrings__my-milvus" && kvp.Value == "Endpoint=http://my-milvus.dev.internal:19530;Key=root:pass");
    }

    [Fact]
    public async Task VerifyManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions() { Args = new string[] { "--publisher", "manifest" } });
        var pass = appBuilder.AddParameter("apikey", "pass");
        var milvus = appBuilder.AddMilvus("milvus", pass);
        var db1 = milvus.AddDatabase("db1");

        var serverManifest = await ManifestUtils.GetManifest(milvus.Resource); // using this method does not get any ExecutionContext.IsPublishMode changes
        var dbManifest = await ManifestUtils.GetManifest(db1.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Endpoint={milvus.bindings.grpc.url};Key=root:{apikey.value}",
              "image": "{{MilvusContainerImageTags.Registry}}/{{MilvusContainerImageTags.Image}}:{{MilvusContainerImageTags.Tag}}",
              "args": [
                "milvus",
                "run",
                "standalone"
              ],
              "env": {
                "COMMON_STORAGETYPE": "local",
                "ETCD_USE_EMBED": "true",
                "ETCD_DATA_DIR": "/var/lib/milvus/etcd",
                "COMMON_SECURITY_AUTHORIZATIONENABLED": "true",
                "COMMON_SECURITY_DEFAULTROOTPASSWORD": "{apikey.value}"
              },
              "bindings": {
                "grpc": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http2",
                  "targetPort": 19530
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "{milvus.connectionString};Database=db1"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public void AddMilvusWithSpecifyingPorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var pass = builder.AddParameter("apikey", "pass");

        var milvus = builder.AddMilvus("my-milvus", grpcPort: 5503, apiKey: pass);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var milvusResource = Assert.Single(appModel.Resources.OfType<MilvusServerResource>());
        Assert.Equal("my-milvus", milvusResource.Name);

        Assert.Single(milvusResource.Annotations.OfType<EndpointAnnotation>());

        var grpcEndpoint = milvusResource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "grpc");
        Assert.Equal(MilvusPortGrpc, grpcEndpoint.TargetPort);
        Assert.False(grpcEndpoint.IsExternal);
        Assert.Equal(5503, grpcEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, grpcEndpoint.Protocol);
        Assert.Equal("http2", grpcEndpoint.Transport);
        Assert.Equal("http", grpcEndpoint.UriScheme);
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}
