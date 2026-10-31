// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikMiddlewareTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDynamicConfigurationIncludesMiddlewares()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var origin = builder.AddParameter("origin", "https://app.dev.localhost");
        var traefik = builder.AddTraefik("traefik");

        var headers = traefik.AddMiddleware("sec", "headers")
            .WithOption("frameDeny", true)
            .WithOption("stsSeconds", 31536000)
            .WithOption("customRequestHeaders.X-Script-Name", "test")
            .WithOption("accessControlAllowOriginList", [ReferenceExpression.Create($"{origin}")]);
        var strip = traefik.AddMiddleware("strip-v1", "stripPrefix")
            .WithOption("prefixes", ["/v1"]);
        var compress = traefik.AddMiddleware("compress", "compress");

        // Defined but used by no route: still written, so it can be referenced later (for example by an entry point).
        traefik.AddMiddleware("unused", "inFlightReq")
            .WithOption("amount", 10);

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.PathPrefix = "/v1";
                route.RouterName = "app-v1";
                route.Middlewares.Add(strip.Resource);
                // Also applied to the whole resource below: runs once, at the resource-level position.
                route.Middlewares.Add(headers.Resource);
            })
            // Resource-level middlewares run first, also for the routes declared before this call.
            .WithTraefikMiddleware(headers, compress);

        using var app = builder.Build();

        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 50001, containerHost: "api", containerPort: 8080);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public void AddMiddlewareAddsChildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var middleware = traefik.AddMiddleware("strip", "stripPrefix");

        Assert.Same(traefik.Resource, middleware.Resource.Parent);
        Assert.Equal("stripPrefix", middleware.Resource.Type);
        Assert.Equal([middleware.Resource], traefik.Resource.Middlewares);
    }

    [Theory]
    [InlineData("strip-prefix")]
    [InlineData("strip.prefix")]
    [InlineData("1strip")]
    public void AddMiddlewareRejectsInvalidTypes(string type)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddMiddleware("m", type));
        Assert.Equal($"The middleware type '{type}' is invalid. Use the type name from the Traefik documentation, for example 'stripPrefix'. (Parameter 'type')", exception.Message);
    }

    [Theory]
    [InlineData("customRequestHeaders", "customRequestHeaders.X-Test", "The option path 'customRequestHeaders.X-Test' conflicts with the value already set at 'customRequestHeaders'. (Parameter 'path')")]
    [InlineData("customRequestHeaders.X-Test", "customRequestHeaders", "The option path 'customRequestHeaders' conflicts with the options already set below it. (Parameter 'path')")]
    [InlineData("prefixes", "prefixes..x", "The option path 'prefixes..x' is invalid. Use names of letters, digits, '-' and '_', separated by '.'. (Parameter 'path')")]
    public void WithOptionRejectsConflictingOrInvalidPaths(string first, string second, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var middleware = builder.AddTraefik("traefik").AddMiddleware("m", "headers")
            .WithOption(first, "value");

        var exception = Assert.Throws<ArgumentException>(() => middleware.WithOption(second, "value"));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void RouteMiddlewareOfAnotherTraefikThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var other = builder.AddTraefik("other");
        var strip = other.AddMiddleware("strip", "stripPrefix");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "api.dev.localhost", strip);

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("The Traefik route 'api' of resource 'api' goes through 'traefik', but its middleware 'strip' belongs to 'other'.", exception.Message);
    }

    [Fact]
    public void ResourceMiddlewareWithoutRouteThroughItsTraefikThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var other = builder.AddTraefik("other");
        var strip = other.AddMiddleware("strip", "stripPrefix");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "api.dev.localhost")
            .WithTraefikMiddleware(strip);

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("Resource 'api' uses the Traefik middleware 'strip' of 'other', but has no route through 'other'.", exception.Message);
    }

    [Fact]
    public async Task RunModeEnvironmentReferencesMiddlewareEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        // A host process that Traefik must reach, for example a forward authentication server.
        var auth = builder.AddExecutable("auth", "dotnet", ".")
            .WithHttpEndpoint(port: 5200);

        traefik.AddMiddleware("auth-check", "forwardAuth")
            .WithOption("address", ReferenceExpression.Create($"{auth.GetEndpoint("http")}/verify"))
            .WithOption("trustForwardHeader", false);

        using var app = builder.Build();

        EndpointAllocation.Allocate(auth.Resource, "http", hostPort: 5200, containerHost: "host.docker.internal", containerPort: 5200);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(traefik.Resource, DistributedApplicationOperation.Run, app.Services);

        Assert.Equal("http://host.docker.internal:5200/verify", env["ASPIRE_TRAEFIK_MIDDLEWARE_AUTH_CHECK_1"]);
    }
}
