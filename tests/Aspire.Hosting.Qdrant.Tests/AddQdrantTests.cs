// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Qdrant.Tests;

public class AddQdrantTests(ITestOutputHelper testOutputHelper)
{
    private const int QdrantPortGrpc = 6334;
    private const int QdrantPortHttp = 6333;
    private static readonly MethodInfo s_polyglotWithReferenceMethod = typeof(ResourceBuilderExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == nameof(ResourceBuilderExtensions.WithReference)
            && m.IsGenericMethodDefinition
            && m.GetParameters() is { Length: 5 } parameters
            && parameters[1].ParameterType == typeof(IResourceBuilder<IResource>));

    [Fact]
    public void AddQdrantAddsGeneratedApiKeyParameterWithUserSecretsParameterDefaultInRunMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var qd = appBuilder.AddQdrant("qd");

        Assert.Equal("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", qd.Resource.ApiKeyParameter.Default?.GetType().FullName);
    }

    [Fact]
    public void AddQdrantDoesNotAddGeneratedPasswordParameterWithUserSecretsParameterDefaultInPublishMode()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var qd = appBuilder.AddQdrant("qd");

        Assert.NotEqual("Aspire.Hosting.ApplicationModel.UserSecretsParameterDefault", qd.Resource.ApiKeyParameter.Default?.GetType().FullName);
    }

    [Fact]
    public async Task AddQdrantWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddQdrant("my-qdrant");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("my-qdrant", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(QdrantContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(QdrantContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(QdrantContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = containerResource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == "grpc");
        Assert.NotNull(endpoint);
        Assert.Equal(QdrantPortGrpc, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("grpc", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http2", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("QDRANT__SERVICE__API_KEY", env.Key);
                Assert.False(string.IsNullOrEmpty(env.Value));
            });
    }

    [Fact]
    public void AddQdrantWithDefaultsAndDashboardAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddQdrant("my-qdrant");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("my-qdrant", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(QdrantContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(QdrantContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(QdrantContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = containerResource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == "http");

        Assert.NotNull(endpoint);
        Assert.Equal(QdrantPortHttp, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("http", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);
    }

    [Fact]
    public async Task AddQdrantAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");
        appBuilder.AddQdrant("my-qdrant", apiKey: pass);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.GetContainerResources());
        Assert.Equal("my-qdrant", containerResource.Name);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(QdrantContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(QdrantContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(QdrantContainerImageTags.Registry, containerAnnotation.Registry);

        var endpoint = containerResource.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => e.Name == "grpc");
        Assert.NotNull(endpoint);
        Assert.Equal(QdrantPortGrpc, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("grpc", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("http2", endpoint.Transport);
        Assert.Equal("http", endpoint.UriScheme);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Collection(config,
            env =>
            {
                Assert.Equal("QDRANT__SERVICE__API_KEY", env.Key);
                Assert.Equal("pass", env.Value);
            });
    }

    [Fact]
    public async Task QdrantCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");

        var qdrant = appBuilder.AddQdrant("my-qdrant", pass)
                                 .WithEndpoint("grpc", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6334));

        var connectionStringResource = qdrant.Resource as IResourceWithConnectionString;

        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal($"Endpoint=http://localhost:6334;Key=pass", connectionString);
    }

    [Fact]
    public async Task QdrantClientAppWithReferenceContainsConnectionStrings()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var pass = appBuilder.AddParameter("pass", "pass");

        var qdrant = appBuilder.AddQdrant("my-qdrant", pass)
            .WithEndpoint("grpc", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6334);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "my-qdrant.dev.internal", 6334, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            })
            .WithEndpoint("http", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6333);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "my-qdrant.dev.internal", 6333, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });

        var projectA = appBuilder.AddProject<ProjectA>("projecta", o => o.ExcludeLaunchProfile = true)
            .WithReference(qdrant);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(2, servicesKeysCount);

        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__my-qdrant" && kvp.Value == "Endpoint=http://localhost:6334;Key=pass");
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__my-qdrant_http" && kvp.Value == "Endpoint=http://localhost:6333;Key=pass");

        var container1 = appBuilder.AddContainer("container1", "fake")
            .WithReference(qdrant);

        // Call environment variable callbacks.
        var containerConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container1.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var containerServicesKeysCount = containerConfig.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(2, containerServicesKeysCount);

        Assert.Contains(containerConfig, kvp => kvp.Key == "ConnectionStrings__my-qdrant" && kvp.Value == "Endpoint=http://my-qdrant.dev.internal:6334;Key=pass");
        Assert.Contains(containerConfig, kvp => kvp.Key == "ConnectionStrings__my-qdrant_http" && kvp.Value == "Endpoint=http://my-qdrant.dev.internal:6333;Key=pass");
    }

    [Fact]
    public async Task WithReferenceDispatchesQdrantReference()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var pass = appBuilder.AddParameter("pass", "pass");

        var qdrant = appBuilder.AddQdrant("my-qdrant", pass)
            .WithEndpoint("grpc", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6334);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "my-qdrant.dev.internal", 6334, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            })
            .WithEndpoint("http", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6333);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "my-qdrant.dev.internal", 6333, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });
        var consumer = appBuilder.AddContainer("consumer", "fake");

        InvokeWithReference(consumer, qdrant);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("Endpoint=http://my-qdrant.dev.internal:6334;Key=pass", config["ConnectionStrings__my-qdrant"]);
        Assert.Equal("Endpoint=http://my-qdrant.dev.internal:6333;Key=pass", config["ConnectionStrings__my-qdrant_http"]);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions() { Args = new string[] { "--publisher", "manifest" } } );
        var qdrant = appBuilder.AddQdrant("qdrant");

        var serverManifest = await ManifestUtils.GetManifest(qdrant.Resource); // using this method does not get any ExecutionContext.IsPublishMode changes

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Endpoint={qdrant.bindings.grpc.url};Key={qdrant-Key.value}",
              "image": "{{QdrantContainerImageTags.Registry}}/{{QdrantContainerImageTags.Image}}:{{QdrantContainerImageTags.Tag}}",
              "env": {
                "QDRANT__SERVICE__API_KEY": "{qdrant-Key.value}",
                "QDRANT__SERVICE__ENABLE_STATIC_CONTENT": "0"
              },
              "bindings": {
                "grpc": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http2",
                  "targetPort": 6334
                },
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 6333
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithParameters()
    {
        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions() { Args = new string[] { "--publisher", "manifest" } });

        var apiKeyParameter = appBuilder.AddParameter("QdrantApiKey");
        var qdrant = appBuilder.AddQdrant("qdrant", apiKeyParameter);

        var serverManifest = await ManifestUtils.GetManifest(qdrant.Resource); // using this method does not get any ExecutionContext.IsPublishMode changes

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "Endpoint={qdrant.bindings.grpc.url};Key={QdrantApiKey.value}",
              "image": "{{QdrantContainerImageTags.Registry}}/{{QdrantContainerImageTags.Image}}:{{QdrantContainerImageTags.Tag}}",
              "env": {
                "QDRANT__SERVICE__API_KEY": "{QdrantApiKey.value}",
                "QDRANT__SERVICE__ENABLE_STATIC_CONTENT": "0"
              },
              "bindings": {
                "grpc": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http2",
                  "targetPort": 6334
                },
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 6333
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, serverManifest.ToString());
    }

    [Fact]
    public void AddQdrantWithSpecifyingPorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var qdrant = builder.AddQdrant("my-qdrant", grpcPort: 5503, httpPort: 5504);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var qdrantResource = Assert.Single(appModel.Resources.OfType<QdrantServerResource>());
        Assert.Equal("my-qdrant", qdrantResource.Name);

        Assert.Equal(2, qdrantResource.Annotations.OfType<EndpointAnnotation>().Count());

        var grpcEndpoint = qdrantResource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "grpc");
        Assert.Equal(6334, grpcEndpoint.TargetPort);
        Assert.False(grpcEndpoint.IsExternal);
        Assert.Equal(5503, grpcEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, grpcEndpoint.Protocol);
        Assert.Equal("http2", grpcEndpoint.Transport);
        Assert.Equal("http", grpcEndpoint.UriScheme);

        var httpEndpoint = qdrantResource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
        Assert.Equal(6333, httpEndpoint.TargetPort);
        Assert.False(httpEndpoint.IsExternal);
        Assert.Equal(5504, httpEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, httpEndpoint.Protocol);
        Assert.Equal("http", httpEndpoint.Transport);
        Assert.Equal("http", httpEndpoint.UriScheme);
    }

    private static IResourceBuilder<TDestination> InvokeWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return (IResourceBuilder<TDestination>)s_polyglotWithReferenceMethod
            .MakeGenericMethod(typeof(TDestination))
            .Invoke(null, [builder, source, connectionName, optional, name])!;
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}
