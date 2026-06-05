// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class KestrelConfigTests
{
    [Fact]
    public async Task SingleKestrelHttpEndpointIsNamedHttpAndOverridesProfile()
    {
        var resource = CreateTestProjectResource<ProjectWithProfileEndpointAndKestrelHttpEndpoint>(
            operation: DistributedApplicationOperation.Run,
            callback: builder =>
            {
                builder.WithHttpEndpoint(5017, name: "ExplicitHttp");
            });

        Assert.Collection(
            resource.Annotations.OfType<EndpointAnnotation>(),
            a =>
            {
                // Endpoint is named "http", because there is only one Kestrel http endpoint
                Assert.Equal("http", a.Name);
                Assert.Equal("http", a.UriScheme);
                Assert.Equal(5002, a.Port);
            },
            a =>
            {
                Assert.Equal("ExplicitHttp", a.Name);
            }
            );

        AllocateTestEndpoints(resource);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // When using Kestrel, we should not be setting ASPNETCORE_URLS at all
        Assert.False(config.ContainsKey("ASPNETCORE_URLS"));

        // Instead, we should be setting the Kestrel override
        Assert.Equal("http://*:port_http", config["Kestrel__Endpoints__http__Url"]);
    }

    [Fact]
    public async Task KestrelHttpEndpointsAreIgnoredWhenFlagIsSet()
    {
        var resource = CreateTestProjectResource<ProjectWithProfileEndpointAndKestrelHttpEndpoint>(
            operation: DistributedApplicationOperation.Run,
            callback: builder =>
            {
                builder.WithHttpEndpoint(5017, name: "ExplicitHttp");
            },
            options => { options.ExcludeKestrelEndpoints = true; });

        Assert.Collection(
            resource.Annotations.OfType<EndpointAnnotation>(),
            a =>
            {
                Assert.Equal("http", a.Name);
                Assert.Equal("http", a.UriScheme);
                Assert.Equal(5031, a.Port);
            },
            a =>
            {
                Assert.Equal("ExplicitHttp", a.Name);
                Assert.Equal("http", a.UriScheme);
                Assert.Equal(5017, a.Port);
            }
            );

        AllocateTestEndpoints(resource);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // We're ignoring Kestrel, so we should be setting ASPNETCORE_URLS
        Assert.Equal("http://localhost:port_http;http://localhost:port_ExplicitHttp", config["ASPNETCORE_URLS"]);

        // And we should not be setting the Kestrel override
        Assert.False(config.ContainsKey("Kestrel__Endpoints__http__Url"));
    }

    [Fact]
    public void SingleKestrelHttpsEndpointIsNamedHttps()
    {
        var resource = CreateTestProjectResource<ProjectWithKestrelHttpsEndpoint>(operation: DistributedApplicationOperation.Run);

        Assert.Collection(
            resource.Annotations.OfType<EndpointAnnotation>(),
            a =>
            {
                // Endpoint is named "https", because there is only one Kestrel https endpoint
                Assert.Equal("https", a.Name);
                Assert.Equal("https", a.UriScheme);
                Assert.Equal(7002, a.Port);
            }
            );
    }

    [Fact]
    public void MultipleKestrelHttpEndpointsKeepTheirNames()
    {
        var resource = CreateTestProjectResource<ProjectWithMultipleHttpKestrelEndpoints>(operation: DistributedApplicationOperation.Run);

        Assert.Collection(
            resource.Annotations.OfType<EndpointAnnotation>(),
            a =>
            {
                // Endpoints keep their config names because there are multiple Kestrel http endpoints
                Assert.Equal("FirstHttpEndpoint", a.Name);
                Assert.Equal("http", a.UriScheme);
                Assert.Equal(5002, a.Port);
                // The Http2 specified in Kestrel EndpointDefaults was overriden at Endpoint level
                Assert.Equal("http", a.Transport);
            },
            a =>
            {
                Assert.Equal("SecondHttpEndpoint", a.Name);
                Assert.Equal("http", a.UriScheme);
                Assert.Equal(5003, a.Port);
                Assert.Equal("http2", a.Transport);
            }
            );
    }

    [Fact]
    public async Task ExplicitEndpointsResultInKestrelOverridesAtRuntime()
    {
        var resource = CreateTestProjectResource<ProjectWithMultipleHttpKestrelEndpoints>(
            operation: DistributedApplicationOperation.Run,
            callback: builder =>
            {
                builder.WithHttpEndpoint(5017, name: "ExplicitProxiedHttp");
                builder.WithHttpEndpoint(5018, name: "ExplicitNoProxyHttp", isProxied: false);
            });

        AllocateTestEndpoints(resource);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Collection(
            config.Where(envVar => envVar.Key.StartsWith("Kestrel__")),
            envVar =>
            {
                Assert.Equal("Kestrel__Endpoints__FirstHttpEndpoint__Url", envVar.Key);
                Assert.Equal("http://*:port_FirstHttpEndpoint", envVar.Value);
            },
            envVar =>
            {
                Assert.Equal("Kestrel__Endpoints__SecondHttpEndpoint__Url", envVar.Key);
                // Note that localhost (from Kestrel config) is preserved at runtime
                Assert.Equal("http://localhost:port_SecondHttpEndpoint", envVar.Value);
            },
            envVar =>
            {
                Assert.Equal("Kestrel__Endpoints__ExplicitProxiedHttp__Url", envVar.Key);
                Assert.Equal("http://*:port_ExplicitProxiedHttp", envVar.Value);
            },
            envVar =>
            {
                Assert.Equal("Kestrel__Endpoints__ExplicitNoProxyHttp__Url", envVar.Key);
                Assert.Equal("http://*:5018", envVar.Value);
            }
            );
    }

    [Fact]
    public async Task VerifyKestrelEndpointManifestGeneration()
    {
        var resource = CreateTestProjectResource<ProjectWithOnlyKestrelHttpEndpoint>();

        var manifest = await ManifestUtils.GetManifest(resource).DefaultTimeout();

        var expectedManifest = """
            {
              "type": "project.v0",
              "path": "another-path",
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                "Kestrel__Endpoints__http__Url": "http://*:{projectName.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 5002
                },
                "https": {
                  "scheme": "https",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 5002
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyMultipleKestrelEndpointsManifestGeneration()
    {
        var resource = CreateTestProjectResource<ProjectWithMultipleHttpKestrelEndpoints>(
            operation: DistributedApplicationOperation.Publish,
            callback: builder =>
            {
                builder.WithHttpEndpoint(5017, name: "ExplicitProxiedHttp");
                builder.WithHttpEndpoint(5018, name: "ExplicitNoProxyHttp", isProxied: false);
            });

        var manifest = await ManifestUtils.GetManifest(resource).DefaultTimeout();

        // Note that unlike in Run mode, SecondHttpEndpoint is using host * instead of localhost
        var expectedManifest = """
            {
              "type": "project.v0",
              "path": "another-path",
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                "Kestrel__Endpoints__FirstHttpEndpoint__Url": "http://*:{projectName.bindings.FirstHttpEndpoint.targetPort}",
                "Kestrel__Endpoints__SecondHttpEndpoint__Url": "http://*:{projectName.bindings.SecondHttpEndpoint.targetPort}",
                "Kestrel__Endpoints__ExplicitProxiedHttp__Url": "http://*:{projectName.bindings.ExplicitProxiedHttp.targetPort}",
                "Kestrel__Endpoints__ExplicitNoProxyHttp__Url": "http://*:{projectName.bindings.ExplicitNoProxyHttp.targetPort}"
              },
              "bindings": {
                "FirstHttpEndpoint": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 5002
                },
                "SecondHttpEndpoint": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http2",
                  "targetPort": 5003
                },
                "https": {
                  "scheme": "https",
                  "protocol": "tcp",
                  "transport": "http2",
                  "targetPort": 5002
                },
                "ExplicitProxiedHttp": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 5017,
                  "targetPort": 8000
                },
                "ExplicitNoProxyHttp": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 5018
                }
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task VerifyKestrelEnvVariablesGetOmittedFromManifestIfExcluded()
    {
        var resource = CreateTestProjectResource<ProjectWithMultipleHttpKestrelEndpoints>(
            operation: DistributedApplicationOperation.Publish,
            callback: builder =>
            {
                builder.WithHttpEndpoint(5017, name: "ExplicitProxiedHttp")
                    .WithHttpEndpoint(5018, name: "ExplicitNoProxyHttp", isProxied: false)
                    // Exclude both a Kestrel endpoint and an explicit endpoint from environment injection
                    // We do it as separate filters to ensure they are combined correctly
                    .WithEndpointsInEnvironment(e => e.Name != "FirstHttpEndpoint")
                    .WithEndpointsInEnvironment(e => e.Name != "ExplicitProxiedHttp");
            });

        var manifest = await ManifestUtils.GetManifest(resource).DefaultTimeout();

        var expectedEnv = """
            {
              "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
              "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
              "Kestrel__Endpoints__SecondHttpEndpoint__Url": "http://*:{projectName.bindings.SecondHttpEndpoint.targetPort}",
              "Kestrel__Endpoints__ExplicitNoProxyHttp__Url": "http://*:{projectName.bindings.ExplicitNoProxyHttp.targetPort}"
            }
            """;

        Assert.Equal(expectedEnv, manifest["env"]!.ToString());
    }

    [Fact]
    public async Task VerifyEndpointLevelKestrelProtocol()
    {
        var resource = CreateTestProjectResource<ProjectWithKestrelEndpointsLevelProtocols>(
            operation: DistributedApplicationOperation.Publish);

        var manifest = await ManifestUtils.GetManifest(resource).DefaultTimeout();

        var expectedBindings = """
            {
              "HttpEndpointUsingHttp2Transport": {
                "scheme": "http",
                "protocol": "tcp",
                "transport": "http2",
                "targetPort": 5002
              },
              "HttpEndpointUsingHttpTransport": {
                "scheme": "http",
                "protocol": "tcp",
                "transport": "http",
                "targetPort": 5003
              },
              "HttpsEndpointUsingHttp2Transport": {
                "scheme": "https",
                "protocol": "tcp",
                "transport": "http2",
                "targetPort": 5002
              },
              "HttpsEndpointUsingHttpTransport": {
                "scheme": "https",
                "protocol": "tcp",
                "transport": "http",
                "targetPort": 7003
              }
            }
            """;

        Assert.Equal(expectedBindings, manifest!["bindings"]!.ToString());
    }

    private static ProjectResource CreateTestProjectResource<TProject>(
        DistributedApplicationOperation operation = DistributedApplicationOperation.Publish,
        Action<IResourceBuilder<ProjectResource>>? callback = null,
        Action<ProjectResourceOptions>? configure = null) where TProject : IProjectMetadata, new()
    {
        var appBuilder = ProjectResourceTests.CreateBuilder(operation: operation);
        var projectBuilder = appBuilder.AddProject<TProject>("projectName", configure ?? (_ => { }));
        if (callback != null)
        {
            callback(projectBuilder);
        }
        DistributedApplication app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var projectResources = appModel.GetProjectResources();
        return Assert.Single(projectResources);
    }

    private sealed class ProjectWithOnlyKestrelHttpEndpoint : ProjectResourceTests.BaseProjectWithProfileAndConfig
    {
        public ProjectWithOnlyKestrelHttpEndpoint()
        {
            JsonConfigString = """
            {
              "Kestrel": {
                "Endpoints": {
                  "SomeHttpEndpoint": { "Url": "http://*:5002" }
                }
              }
            }
            """;
        }
    }

    private sealed class ProjectWithProfileEndpointAndKestrelHttpEndpoint : ProjectResourceTests.BaseProjectWithProfileAndConfig
    {
        public ProjectWithProfileEndpointAndKestrelHttpEndpoint()
        {
            Profiles = new()
            {
                ["OnlyHttp"] = new()
                {
                    ApplicationUrl = "http://localhost:5031",
                }
            };
            JsonConfigString = """
            {
              "Kestrel": {
                "Endpoints": {
                  "SomeHttpEndpoint": { "Url": "http://*:5002" }
                }
              }
            }
            """;
        }
    }

    private sealed class ProjectWithKestrelHttpsEndpoint : ProjectResourceTests.BaseProjectWithProfileAndConfig
    {
        public ProjectWithKestrelHttpsEndpoint()
        {
            JsonConfigString = """
            {
              "Kestrel": {
                "Endpoints": {
                  "SomeHttpsEndpoint": { "Url": "https://*:7002" }
                }
              }
            }
            """;
        }
    }

    private sealed class ProjectWithMultipleHttpKestrelEndpoints : ProjectResourceTests.BaseProjectWithProfileAndConfig
    {
        public ProjectWithMultipleHttpKestrelEndpoints()
        {
            JsonConfigString = """
            {
              "Kestrel": {
                "EndpointDefaults": {
                  "Protocols": "Http2"
                },
                "Endpoints": {
                  "FirstHttpEndpoint": { "Url": "http://*:5002", "Protocols": "Http" },
                  "SecondHttpEndpoint": { "Url": "http://localhost:5003" }
                }
              }
            }
            """;
        }
    }

    private sealed class ProjectWithKestrelEndpointsLevelProtocols : ProjectResourceTests.BaseProjectWithProfileAndConfig
    {
        public ProjectWithKestrelEndpointsLevelProtocols()
        {
            JsonConfigString = """
            {
              "Kestrel": {
                "Endpoints": {
                  "HttpEndpointUsingHttp2Transport": { "Url": "http://*:5002", "Protocols": "Http2" },
                  "HttpEndpointUsingHttpTransport": { "Url": "http://*:5003" },
                  "HttpsEndpointUsingHttp2Transport": { "Url": "https://*:7002", "Protocols": "Http2" },
                  "HttpsEndpointUsingHttpTransport": { "Url": "https://*:7003" }
                }
              }
            }
            """;
        }
    }

    private static void AllocateTestEndpoints(ProjectResource resource)
    {
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", endpoint.Port ?? 0, targetPortExpression: $"port_{endpoint.Name}");
        }
    }
}
