// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikRoutingTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDynamicConfigurationRoutesToContainersAndHostProcesses()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var domain = builder.AddParameter("domain", "dev.localhost");
        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.{domain}");
                route.PathPrefix = "/api";
                route.Priority = 100;
                route.RouterName = "app-api";
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 5123, containerHost: "host.docker.internal", containerPort: 5123);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        TraefikRoutingSubscriber.ApplyRoutes(model, isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task RunModeDynamicConfigurationUsesWebSecureWhenTlsIsConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task RunModeEnvironmentReferencesEveryBackend()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.dev.localhost");
                route.RouterName = "api-v1";
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 5123, containerHost: "host.docker.internal", containerPort: 5123);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(traefik.Resource, DistributedApplicationOperation.Run, app.Services);

        Assert.Equal("http://whoami:80", env["ASPIRE_TRAEFIK_BACKEND_WHOAMI"]);
        Assert.Equal("http://host.docker.internal:5123", env["ASPIRE_TRAEFIK_BACKEND_API_V1"]);
    }

    [Fact]
    public async Task GetTraefikUrlIncludesHostPortInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.PathPrefix = "/api";
            });

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("http://app.dev.localhost:8000/api", url);
    }

    [Fact]
    public async Task GetTraefikUrlUsesHttpsWithLetsEncryptInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("https://api.dev.localhost:8443", url);
    }

    [Fact]
    public async Task GetTraefikUrlByRouterNameUsesThatRoutesHostAndEntryPoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var app = builder.AddContainer("app", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "app.dev.localhost")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"plain.dev.localhost");
                route.EntryPoint = TraefikResource.WebEntryPointName;
                route.RouterName = "app-plain";
            });

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);
        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        Assert.Equal("https://app.dev.localhost:8443", await app.GetTraefikUrl().GetValueAsync(CancellationToken.None));
        Assert.Equal("http://plain.dev.localhost:8000", await app.GetTraefikUrl("app-plain").GetValueAsync(CancellationToken.None));

        var exception = Assert.Throws<InvalidOperationException>(() => app.GetTraefikUrl("missing"));
        Assert.Equal("Resource 'app' has no Traefik route with the router name 'missing'. Set TraefikRouteOptions.RouterName on the route.", exception.Message);
    }

    [Fact]
    public async Task GetTraefikUrlFollowsTlsConfiguredAfterIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        var url = api.GetTraefikUrl();

        traefik.WithLetsEncrypt("admin@example.com");
        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);
        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        Assert.Equal("https://api.dev.localhost:8443", await url.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TraefikUrlsWithDifferentSchemesHaveDifferentManifestNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var secure = builder.AddContainer("secure", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var plain = builder.AddContainer("plain", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.example.com");
                route.EntryPoint = "web";
            });

        var secureUrl = secure.GetTraefikUrl();
        var plainUrl = plain.GetTraefikUrl();

        // The manifest writes each conditional once per name, so the two schemes must not share one.
        Assert.NotEqual(secureUrl.ValueExpression, plainUrl.ValueExpression);
        Assert.Equal("https://api.example.com", await secureUrl.GetValueAsync(CancellationToken.None));
        Assert.Equal("http://api.example.com", await plainUrl.GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false, null, "https://api.example.com")]
    [InlineData(true, "web", "http://api.example.com")]
    [InlineData(true, "websecure", "https://api.example.com")]
    public async Task GetTraefikUrlAtPublishTime(bool external, string? externalEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik");
        if (external)
        {
            traefik.PublishAsExternal("traefik-public", externalEntryPoint!);
        }
        else
        {
            traefik.WithLetsEncrypt("admin@example.com");
        }

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal(expected, url);
    }

    [Fact]
    public async Task GetTraefikUrlAssumesHttpsForParameterizedExternalEntryPoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var entryPoint = builder.AddParameter("traefik-entrypoint");
        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", ReferenceExpression.Create($"{entryPoint}"));

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("https://api.example.com", url);
    }

    [Theory]
    [InlineData(true, "https", "https://api.example.com")]
    [InlineData(true, "web", "http://api.example.com")]
    [InlineData(false, "websecure", "https://api.example.com")]
    [InlineData(false, "web", "http://api.example.com")]
    public async Task GetTraefikUrlOfRouteWithEntryPointAtPublishTime(bool external, string routeEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik");
        if (external)
        {
            // An external Traefik's HTTPS entry point often isn't called `websecure`. A route that names it explicitly
            // follows the same rule as the default entry point: only `web` is plain HTTP.
            traefik.PublishAsExternal("traefik-public", "websecure");
        }
        else
        {
            traefik.WithLetsEncrypt("admin@example.com");
        }

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.example.com");
                route.EntryPoint = routeEntryPoint;
            });

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null, "https://api.example.com:8443")]
    [InlineData("http", "http://api.example.com")]
    [InlineData("edge", "https://api.example.com")]
    [InlineData("web", "http://api.example.com")]
    public async Task GetTraefikUrlUsesDescribedExternalEntryPoints(string? routeEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // `http` isn't called `web`, so only its description makes it plain HTTP. `edge` isn't described and follows
        // the naming convention.
        var traefik = builder.AddTraefik("traefik")
            .WithExternalEntryPoint("http", isTls: false)
            .WithExternalEntryPoint("https", isTls: true, port: 8443)
            .PublishAsExternal("traefik-public", "https");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.example.com");
                route.EntryPoint = routeEntryPoint;
            });

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(false, "https://api.example.com:8443")]
    [InlineData(true, "https://api.example.com:9443")]
    public async Task GetTraefikUrlIncludesNonStandardPortAtPublishTime(bool customEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithHostHttpsPort(8443)
            .WithEntryPoint("admin", targetPort: 9443, scheme: "https");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.example.com");
                route.EntryPoint = customEntryPoint ? "admin" : null;
            });

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal(expected, url);
    }

    [Fact]
    public async Task GetTraefikUrlOfHttpsEntryPointUsesHttpsInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithEntryPoint("admin", targetPort: 9443, scheme: "https");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.dev.localhost");
                route.EntryPoint = "admin";
            });

        EndpointAllocation.Allocate(traefik.Resource, "admin", hostPort: 9443, containerHost: "traefik", containerPort: 9443);

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("https://api.dev.localhost:9443", url);
    }

    [Fact]
    public async Task PublishedTraefikUrlBuiltBeforeItsPortChangedThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var url = api.GetTraefikUrl();
        traefik.WithHostHttpsPort(8443);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await url.GetValueAsync(CancellationToken.None));
        Assert.Equal("A Traefik URL of 'traefik' was built for the standard port, but its entry point is now published on port 8443. Configure Traefik's ports and entry points (WithHostPort, WithHostHttpsPort, WithEntryPoint, WithExternalEntryPoint) before calling GetTraefikUrl.", exception.Message);
    }

    [Theory]
    [InlineData("bad.name", null, typeof(ArgumentException), "The entry point name 'bad.name' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'name')")]
    [InlineData("https", 0, typeof(ArgumentOutOfRangeException), "port ('0') must be greater than or equal to '1'. (Parameter 'port')\nActual value was 0.")]
    public void InvalidExternalEntryPointThrows(string name, int? port, Type exceptionType, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws(exceptionType, () => traefik.WithExternalEntryPoint(name, isTls: true, port));
        Assert.Equal(message, exception.Message.ReplaceLineEndings("\n"));
        Assert.Empty(traefik.Resource.ExternalEntryPoints);
    }

    [Fact]
    public async Task RawRuleWithHostKeepsRuleAndGivesPublicUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"a.example.com");
                route.RawRule = "Host(`a.example.com`) || Host(`b.example.com`)";
            });

        var options = api.Resource.Annotations.OfType<TraefikRouteAnnotation>().Single().Options;

        Assert.Equal("Host(`a.example.com`) || Host(`b.example.com`)", await TraefikRouteRule.Build(options).GetValueAsync(CancellationToken.None));
        Assert.Equal("http://a.example.com", await api.GetTraefikUrl().GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("web secure", null, "The entry point name 'web secure' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'configure')")]
    [InlineData(null, "le$staging", "The certificate resolver name 'le$staging' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'configure')")]
    public void InvalidRouteEntryPointOrCertResolverThrows(string? entryPoint, string? certResolver, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<ArgumentException>(() => api.WithTraefikRoute(traefik, route =>
        {
            route.Host = ReferenceExpression.Create($"api.dev.localhost");
            route.EntryPoint = entryPoint;
            route.CertResolver = certResolver;
        }));
        Assert.Equal(message, exception.Message);
        Assert.Empty(api.Resource.Annotations.OfType<TraefikRouteAnnotation>());
    }

    [Fact]
    public void InvalidTransportRouteAndExternalNamesThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var echo = builder.AddContainer("echo", "image").WithEndpoint(targetPort: 8080, name: "tcp");

        var tcp = Assert.Throws<ArgumentException>(() => echo.WithTraefikTcpRoute(traefik, "echo$tcp"));
        Assert.Equal("The entry point name 'echo$tcp' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'configure')", tcp.Message);

        var udp = Assert.Throws<ArgumentException>(() => echo.WithTraefikUdpRoute(traefik, "echo udp"));
        Assert.Equal("The entry point name 'echo udp' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'entryPoint')", udp.Message);

        var external = Assert.Throws<ArgumentException>(() => traefik.PublishAsExternal("traefik-public", "websecure", certResolver: "le.prod"));
        Assert.Equal("The certificate resolver name 'le.prod' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'certResolver')", external.Message);
    }

    [Fact]
    public void GetTraefikUrlWithoutRouteThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080);

        var exception = Assert.Throws<InvalidOperationException>(() => api.GetTraefikUrl());
        Assert.Equal("Resource 'api' has no Traefik route. Call WithTraefikRoute first.", exception.Message);
    }

    [Theory]
    [InlineData("shared", "Shared")]
    // Both map to the ASPIRE_TRAEFIK_*_API_V1 variables that carry the backend address and port.
    [InlineData("api-v1", "api_v1")]
    public void DuplicateRouterNamesThrow(string first, string second)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("a", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"a.dev.localhost");
                route.RouterName = first;
            });

        builder.AddContainer("b", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"b.dev.localhost");
                route.RouterName = second;
            });

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal($"The Traefik router name '{second}' of resource 'b' clashes with '{first}' of 'a'. Set TraefikRouteOptions.RouterName to make it unique.", exception.Message);
    }

    [Fact]
    public void SecondRouteOfResourceGetsNumberedRouterName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "api.dev.localhost")
            .WithTraefikRoute(traefik, "api2.dev.localhost");

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        Assert.Collection(traefik.Resource.Routes,
            route => Assert.Equal("api", route.RouterName),
            route => Assert.Equal("api-2", route.RouterName));
    }

    [Fact]
    public void RouteToResourceWithSeveralUnnamedHttpEndpointsThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "public", targetPort: 80)
            .WithHttpEndpoint(name: "admin", targetPort: 81)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("Resource 'api' has several HTTP endpoints and none is named 'http'. Set TraefikRouteOptions.EndpointName to choose one.", exception.Message);
    }

    [Theory]
    [InlineData(null, null, "A Traefik route needs a matcher, for example a host or a path prefix. (Parameter 'configure')")]
    [InlineData(null, "api", "The path 'api' must start with '/'. (Parameter 'configure')")]
    [InlineData("api.dev.localhost", "/a`b", "The value '/a`b' of the PathPrefix matcher can't contain '`'. (Parameter 'configure')")]
    public void InvalidRouteOptionsThrow(string? host, string? pathPrefix, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<ArgumentException>(() => api.WithTraefikRoute(traefik, route =>
        {
            route.Host = host is null ? null : ReferenceExpression.Create($"{host}");
            route.PathPrefix = pathPrefix;
        }));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task RoutedUrlReplacesDirectUrlsInResourceTable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        using var app = builder.Build();

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var urls = await GetUrlsAsync(app, api.Resource, "http://localhost:5123");

        Assert.Collection(urls,
            url =>
            {
                Assert.Equal("http://localhost:5123", url.Url);
                Assert.Equal(UrlDisplayLocation.DetailsOnly, url.DisplayLocation);
            },
            url =>
            {
                Assert.Equal("http://api.dev.localhost:8000", url.Url);
                Assert.Equal("http://api.dev.localhost:8000 (Traefik)", url.DisplayText);
                Assert.Equal(UrlDisplayLocation.SummaryAndDetails, url.DisplayLocation);
            });
    }

    [Fact]
    public async Task PathOnlyRouteKeepsDirectUrlsInResourceTable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, route => route.Path = "/basicauth");

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var urls = await GetUrlsAsync(app, api.Resource, "http://localhost:5123");

        var url = Assert.Single(urls);
        Assert.Equal("http://localhost:5123", url.Url);
        Assert.Equal(UrlDisplayLocation.SummaryAndDetails, url.DisplayLocation);
    }

    private static async Task<List<ResourceUrlAnnotation>> GetUrlsAsync(DistributedApplication app, IResource resource, string directUrl)
    {
        // The dashboard runs the URL callbacks over the URLs of the allocated endpoints, so start from the direct one.
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var context = new ResourceUrlsCallbackContext(executionContext, resource, [new ResourceUrlAnnotation { Url = directUrl }]);
        foreach (var callback in resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return context.Urls;
    }
}
