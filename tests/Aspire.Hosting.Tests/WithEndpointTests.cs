// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithEndpointTests
{
    // copied from /src/Shared/StringComparers.cs to avoid ambiguous reference since StringComparers exists internally in multiple Hosting assemblies.
    private static StringComparison EndpointAnnotationName => StringComparison.OrdinalIgnoreCase;

    [Fact]
    public void WithEndpointInvokesCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint(3000, 1000, name: "mybinding")
                              .WithEndpoint("mybinding", endpoint =>
                              {
                                  endpoint.Port = 2000;
                              });

        var endpoint = projectA.Resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => string.Equals(e.Name, "mybinding", EndpointAnnotationName)).Single();
        Assert.Equal(2000, endpoint.Port);
    }

    [Fact]
    public void WithEndpointMakesTargetPortEqualToPortIfProxyless()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint("mybinding", endpoint =>
                              {
                                  endpoint.Port = 2000;
                                  endpoint.IsProxied = false;
                              });

        var endpoint = projectA.Resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => string.Equals(e.Name, "mybinding", EndpointAnnotationName)).Single();

        // It should fall back to the Port value since TargetPort was not set
        Assert.Equal(2000, endpoint.TargetPort);

        // In Proxy mode, the fallback should not happen
        endpoint.IsProxied = true;
        Assert.Null(endpoint.TargetPort);

        // Back in proxy-less mode, it should fall back again
        endpoint.IsProxied = false;
        Assert.Equal(2000, endpoint.TargetPort);

        // Setting it to null explicitly should disable the override mechanism
        endpoint.TargetPort = null;
        Assert.Null(endpoint.TargetPort);

        // No fallback when setting TargetPort explicitly
        endpoint.TargetPort = 2001;
        Assert.Equal(2001, endpoint.TargetPort);
    }

    [Fact]
    public void WithEndpointMakesPortEqualToTargetPortIfProxyless()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint("mybinding", endpoint =>
                              {
                                  endpoint.TargetPort = 2000;
                                  endpoint.IsProxied = false;
                              });

        var endpoint = projectA.Resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => string.Equals(e.Name, "mybinding", EndpointAnnotationName)).Single();

        // It should fall back to the TargetPort value since Port was not set
        Assert.Equal(2000, endpoint.Port);

        // In Proxy mode, the fallback should not happen
        endpoint.IsProxied = true;
        Assert.Null(endpoint.Port);

        // Back in proxy-less mode, it should fall back again
        endpoint.IsProxied = false;
        Assert.Equal(2000, endpoint.Port);

        // Setting it to null explicitly should disable the override mechanism
        endpoint.Port = null;
        Assert.Null(endpoint.Port);

        // No fallback when setting Port explicitly
        endpoint.Port = 2001;
        Assert.Equal(2001, endpoint.Port);
    }

    [Fact]
    public void WithEndpointCallbackDoesNotRunIfEndpointDoesntExistAndCreateIfNotExistsIsFalse()
    {
        var executed = false;

        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint("mybinding", endpoint =>
                              {
                                  executed = true;
                              },
                              createIfNotExists: false);

        Assert.False(executed);
        Assert.False(projectA.Resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var annotations));
    }

    [Fact]
    public void WithEndpointCallbackRunsIfEndpointDoesntExistAndCreateIfNotExistsIsDefault()
    {
        var executed = false;

        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint("mybinding", endpoint =>
                              {
                                  executed = true;
                              });

        Assert.True(executed);
        Assert.True(projectA.Resource.TryGetAnnotationsOfType<EndpointAnnotation>(out _));
    }

    [Fact]
    public void WithEndpointCallbackRunsIfEndpointDoesntExistAndCreateIfNotExistsIsTrue()
    {
        var executed = false;

        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta").WithEndpoint("mybinding", endpoint =>
        {
            executed = true;
        },
        createIfNotExists: true);

        Assert.True(executed);
        Assert.True(projectA.Resource.TryGetAnnotationsOfType<EndpointAnnotation>(out _));
    }

    [Fact]
    public void WithHttpEndpointCallbackCreatesHttpEndpointWhenMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("app", "image")
            .WithHttpEndpointCallback(_ => { }, name: "callback-http");

        var endpoint = container.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(endpoint => string.Equals(endpoint.Name, "callback-http", EndpointAnnotationName));

        Assert.Equal("http", endpoint.UriScheme);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
    }

    [Fact]
    public void WithHttpsEndpointCallbackCreatesHttpsEndpointWhenMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("app", "image")
            .WithHttpsEndpointCallback(_ => { }, name: "callback-https");

        var endpoint = container.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(endpoint => string.Equals(endpoint.Name, "callback-https", EndpointAnnotationName));

        Assert.Equal("https", endpoint.UriScheme);
        Assert.Equal("http", endpoint.Transport);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
    }

    [Fact]
    public void EndpointsWithTwoPortsSameNameUpdatesExisting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddProject<ProjectA>("projecta")
                .WithHttpsEndpoint(3000, 1000, name: "mybinding")
                .WithHttpsEndpoint(3000, 2000, name: "mybinding");

        var resource = Assert.Single(builder.Resources.OfType<ProjectResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "mybinding");
        Assert.Equal(3000, endpoint.Port);
        Assert.Equal(2000, endpoint.TargetPort);
    }

    [Fact]
    public void AddingTwoEndpointsWithDefaultNamesUpdatesExisting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddProject<ProjectA>("projecta")
                .WithHttpsEndpoint(3000, 1000)
                .WithHttpsEndpoint(3000, 2000);

        var resource = Assert.Single(builder.Resources.OfType<ProjectResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "https");
        Assert.Equal(3000, endpoint.Port);
        Assert.Equal(2000, endpoint.TargetPort);
    }

    [Fact]
    public void EndpointsWithSinglePortSameNameUpdatesExisting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddProject<ProjectB>("projectb")
               .WithHttpsEndpoint(1000, name: "mybinding")
               .WithHttpsEndpoint(2000, name: "mybinding");

        var resource = Assert.Single(builder.Resources.OfType<ProjectResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "mybinding");
        Assert.Equal(2000, endpoint.Port);
    }

    [Fact]
    public async Task CanAddEndpointsWithContainerPortAndEnv()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddExecutable("foo", "foo", ".")
               .WithHttpEndpoint(targetPort: 3001, name: "mybinding", env: "PORT");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var exeResources = appModel.GetExecutableResources();

        var resource = Assert.Single(exeResources);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("foo", resource.Name);
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToArray();
        Assert.Single(endpoints);
        Assert.Equal("mybinding", endpoints[0].Name);
        Assert.Equal(3001, endpoints[0].TargetPort);
        Assert.Equal("http", endpoints[0].UriScheme);
        Assert.Equal("3001", config["PORT"]);
    }

    [Fact]
    public void WithExternalHttpEndpointsMarkExistingHttpEndpointsAsExternal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithEndpoint(name: "ep0")
                               .WithHttpEndpoint(name: "ep1")
                               .WithHttpsEndpoint(name: "ep2")
                               .WithExternalHttpEndpoints();

        var ep0 = container.GetEndpoint("ep0");
        var ep1 = container.GetEndpoint("ep1");
        var ep2 = container.GetEndpoint("ep2");

        Assert.False(ep0.EndpointAnnotation.IsExternal);
        Assert.True(ep1.EndpointAnnotation.IsExternal);
        Assert.True(ep2.EndpointAnnotation.IsExternal);
    }

    // Existing code...

    [Fact]
    public async Task VerifyManifestWithBothDifferentPortAndTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithEndpoint(name: "ep0", port: 8080, targetPort: 3000);

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "ep0": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "port": 8080,
                  "targetPort": 3000
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithHttpPortWithTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithHttpEndpoint(name: "h1", targetPort: 3001);

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "h1": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 3001
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithHttpsAndTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithHttpsEndpoint(name: "h2", targetPort: 3001);

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "h2": {
                  "scheme": "https",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 3001
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestContainerWithHttpEndpointAndNoPortsAllocatesPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithHttpEndpoint(name: "h3");

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "h3": {
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
    public async Task VerifyManifestContainerWithHttpsEndpointAllocatesPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithHttpsEndpoint(name: "h4");

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "h4": {
                  "scheme": "https",
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
    public async Task VerifyManifestWithHttpEndpointAndPortOnlySetsTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithHttpEndpoint(name: "otlp", port: 1004);

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "otlp": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 1004
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestWithTcpEndpointAndNoPortAllocatesPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("app", "image")
                               .WithEndpoint(name: "custom");

        var manifest = await ManifestUtils.GetManifest(container.Resource).DefaultTimeout();
        var expectedManifest =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "custom": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8000
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestProjectWithDefaultHttpEndpointsDoesNotAllocatePort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<TestProject>("proj")
            .WithHttpEndpoint(name: "hp")       // Won't get targetPort since it's the first http
            .WithHttpEndpoint(name: "hp2")      // Will get a targetPort
            .WithHttpsEndpoint(name: "hps")     // Won't get targetPort since it's the first https
            .WithHttpsEndpoint(name: "hps2")   // Will get a targetPort
            .WithEndpoint(scheme: "tcp", name: "tcp0");  // Will get a targetPort

        var manifest = await ManifestUtils.GetManifest(project.Resource).DefaultTimeout();

        var expectedManifest =
            """
            {
              "type": "project.v0",
              "path": "projectpath",
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                "HTTP_PORTS": "{proj.bindings.hp.targetPort};{proj.bindings.hp2.targetPort}",
                "HTTPS_PORTS": "{proj.bindings.hps.targetPort};{proj.bindings.hps2.targetPort}"
              },
              "bindings": {
                "hp": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http"
                },
                "hp2": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 8000
                },
                "hps": {
                  "scheme": "https",
                  "protocol": "tcp",
                  "transport": "http"
                },
                "hps2": {
                  "scheme": "https",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 8001
                },
                "tcp0": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8002
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifestProjectWithEndpointsSetsPortsEnvVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<TestProject>("proj")
            .WithHttpEndpoint()
            .WithHttpEndpoint(name: "hp1", port: 5001)
            .WithHttpEndpoint(name: "hp2", port: 5002, targetPort: 5003)
            .WithHttpEndpoint(name: "hp3", targetPort: 5004)
            .WithHttpEndpoint(name: "hp4")
            .WithHttpEndpoint(name: "dontinjectme")
            .WithHttpsEndpoint()
            .WithHttpsEndpoint(name: "hps1", port: 7001)
            .WithHttpsEndpoint(name: "hps2", port: 7002, targetPort: 7003)
            .WithHttpsEndpoint(name: "hps3", targetPort: 7004)
            .WithHttpsEndpoint(name: "hps4", targetPort: 7005)
            // Should not be included in HTTP_PORTS
            .WithEndpointsInEnvironment(e => e.Name != "dontinjectme");

        var manifest = await ManifestUtils.GetManifest(project.Resource).DefaultTimeout();

        var expectedEnv =
            """
            {
              "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
              "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
              "HTTP_PORTS": "{proj.bindings.http.targetPort};{proj.bindings.hp1.targetPort};{proj.bindings.hp2.targetPort};{proj.bindings.hp3.targetPort};{proj.bindings.hp4.targetPort}",
              "HTTPS_PORTS": "{proj.bindings.https.targetPort};{proj.bindings.hps1.targetPort};{proj.bindings.hps2.targetPort};{proj.bindings.hps3.targetPort};{proj.bindings.hps4.targetPort}"
            }
            """;

        Assert.Equal(expectedEnv, manifest["env"]!.ToString());
    }

    [Fact]
    public async Task VerifyManifestPortAllocationIsGlobal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container0 = builder.AddContainer("app0", "image")
                               .WithEndpoint(name: "custom");

        var container1 = builder.AddContainer("app1", "image")
                               .WithEndpoint(name: "custom");

        var manifests = await ManifestUtils.GetManifests([container0.Resource, container1.Resource]).DefaultTimeout();
        var expectedManifest0 =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "custom": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8000
                }
              }
            }
            """;

        var expectedManifest1 =
            """
            {
              "type": "container.v0",
              "image": "image:latest",
              "bindings": {
                "custom": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 8001
                }
              }
            }
            """;

        Assert.Equal(expectedManifest0, manifests[0].ToString());
        Assert.Equal(expectedManifest1, manifests[1].ToString());
    }

    [Fact]
    public void WithEndpoint_WithAllArguments_ForwardsAllArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithEndpoint(123, 456, "scheme", "mybinding", "env", true, true);

        var endpoint = projectA.Resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => string.Equals(e.Name, "mybinding", EndpointAnnotationName)).Single();

        Assert.Equal(123, endpoint.Port);
        Assert.Equal(456, endpoint.TargetPort);
        Assert.Equal("scheme", endpoint.UriScheme);
        Assert.Equal("env", endpoint.TargetPortEnvironmentVariable);
        Assert.True(endpoint.IsProxied);
        Assert.True(endpoint.IsExternal);
        Assert.Equal(System.Net.Sockets.ProtocolType.Tcp, endpoint.Protocol);
    }

    [Fact]
    public async Task LocalhostTopLevelDomainSetsAnnotationValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tcs = new TaskCompletionSource();
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpsEndpoint()
            .WithEndpoint("https", e => e.TargetHost = "example.localhost", createIfNotExists: false)
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task;

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Collection(urls,
            url => Assert.StartsWith("https://example.localhost:", url.Url),
            url => Assert.StartsWith("https://localhost:", url.Url));

        EndpointAnnotation endpoint = Assert.Single(projectA.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.NotNull(endpoint.AllocatedEndpoint);
        Assert.Equal(EndpointBindingMode.SingleAddress, endpoint.AllocatedEndpoint.BindingMode);
        Assert.Equal("localhost", endpoint.AllocatedEndpoint.Address);

        await app.StopAsync();
    }

    [Theory]
    [InlineData("0.0.0.0", EndpointBindingMode.IPv4AnyAddresses)]
    //[InlineData("::", EndpointBindingMode.IPv6AnyAddresses)] // Need to figure out a good way to check that Ipv6 binding is supported
    public async Task TopLevelDomainSetsAnnotationValues(string host, EndpointBindingMode endpointBindingMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tcs = new TaskCompletionSource();
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpsEndpoint()
            .WithEndpoint("https", e => e.TargetHost = host, createIfNotExists: false)
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task;

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Collection(urls,
            url => Assert.StartsWith("https://localhost:", url.Url),
            url => Assert.StartsWith($"https://{Environment.MachineName}:", url.Url));

        EndpointAnnotation endpoint = Assert.Single(projectA.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.NotNull(endpoint.AllocatedEndpoint);
        Assert.Equal(endpointBindingMode, endpoint.AllocatedEndpoint.BindingMode);
        Assert.Equal("localhost", endpoint.AllocatedEndpoint.Address);

        await app.StopAsync();
    }

    [Fact]
    public async Task VerifyManifestProjectWithExplicitPortAndNoLaunchProfile()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<TestProjectNoLaunchSettings>("proj", launchProfileName: null)
            .WithHttpEndpoint(port: 5001);

        var manifest = await ManifestUtils.GetManifest(project.Resource).DefaultTimeout();

        var expectedManifest =
            """
            {
              "type": "project.v0",
              "path": "projectpath",
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                "HTTP_PORTS": "{proj.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 5001
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "projectpath";

        public LaunchSettings? LaunchSettings { get; } = new();
    }

    private sealed class TestProjectNoLaunchSettings : IProjectMetadata
    {
        public string ProjectPath => "projectpath";

        public LaunchSettings? LaunchSettings => null;
    }

    [Fact]
    public void WithHttpEndpointUpdatesPortOnExistingEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint()
            .WithHttpEndpoint(port: 5000);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(5000, endpoint.Port);
    }

    [Fact]
    public void WithHttpsEndpointUpdatesPortOnExistingEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpsEndpoint()
            .WithHttpsEndpoint(port: 5001);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "https");
        Assert.Equal(5001, endpoint.Port);
    }

    [Fact]
    public void WithHttpEndpointUpdatePreservesExistingTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithHttpEndpoint(port: 5000);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(5000, endpoint.Port);
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Fact]
    public void WithHttpEndpointUpdatePreservesExistingEnvVar()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(env: "PORT")
            .WithHttpEndpoint(port: 3000);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(3000, endpoint.Port);
        Assert.Equal("PORT", endpoint.TargetPortEnvironmentVariable);
    }

    [Fact]
    public void WithHttpEndpointUpdatePreservesIsProxiedFalse()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(port: 8080, isProxied: false)
            .WithHttpEndpoint(targetPort: 3000);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(8080, endpoint.Port);
        Assert.Equal(3000, endpoint.TargetPort);
        Assert.False(endpoint.IsProxied);
    }

    [Fact]
    public void WithHttpEndpointUpdateDoesNotDuplicateEnvAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(env: "PORT")
            .WithHttpEndpoint(port: 3000, env: "PORT");

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var envAnnotations = resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToList();
        // Should have exactly one env callback for PORT, not two
        Assert.Single(envAnnotations);
    }

    [Fact]
    public async Task WithHttpEndpointUpdateReplacesEnvVar()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddExecutable("foo", "foo", ".")
            .WithHttpEndpoint(targetPort: 3001, env: "PORT")
            .WithHttpEndpoint(env: "NEW_PORT");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.GetExecutableResources());

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // NEW_PORT should be set, PORT should not
        Assert.True(config.ContainsKey("NEW_PORT"));
        Assert.False(config.ContainsKey("PORT"));
    }

    [Fact]
    public void WithEndpointUpdateDoesNotChangeScheme()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(name: "api")
            .WithHttpsEndpoint(name: "api", port: 8443);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "api");
        // Scheme should remain "http" from the first call
        Assert.Equal("http", endpoint.UriScheme);
        Assert.Equal(8443, endpoint.Port);
    }

    [Fact]
    public void WithEndpointUpdateDoesNotChangeIsProxiedBackToTrue()
    {
        var builder = DistributedApplication.CreateBuilder();

        // isProxied defaults to true in the method signature, so passing true
        // on update can't be distinguished from the default — it's a no-op.
        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(port: 8080, isProxied: false)
            .WithHttpEndpoint(port: 9090, isProxied: true);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(9090, endpoint.Port);
        Assert.False(endpoint.IsProxied);
    }

    [Fact]
    public void WithEndpointUpdateCanSetIsProxiedToFalse()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithHttpEndpoint(port: 8080)
            .WithHttpEndpoint(isProxied: false);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(8080, endpoint.Port);
        Assert.False(endpoint.IsProxied);
    }

    [Fact]
    public void WithEndpointUpdateChangesIsExternal()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("mycontainer", "myimage")
            .WithEndpoint(scheme: "http", name: "http")
            .WithEndpoint(name: "http", isExternal: true);

        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.True(endpoint.IsExternal);
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class ProjectB : IProjectMetadata
    {
        public string ProjectPath => "projectB";
        public LaunchSettings LaunchSettings { get; } = new();
    }
}
