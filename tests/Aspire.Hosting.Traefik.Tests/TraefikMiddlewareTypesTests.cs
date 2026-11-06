// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikMiddlewareTypesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDynamicConfigurationRendersTypedMiddlewares()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var htpasswd = builder.AddParameter("admin-htpasswd", "admin:$2y$05$abcdefghijklmnopqrstuv");
        var password = builder.AddParameter("dev-password", "password", secret: true);

        var traefik = builder.AddTraefik("traefik");

        traefik.AddStripPrefix("strip", "/api", "/v1");
        traefik.AddStripPrefixRegex("strip-regex", "/users/[a-z0-9]+");
        traefik.AddAddPrefix("add-prefix", "/backend");
        traefik.AddReplacePath("replace-path", "/health");
        traefik.AddReplacePathRegex("replace-path-regex", "^/v1/(.*)", "/$1");
        traefik.AddRedirectScheme("to-https", port: 8443);
        traefik.AddRedirectRegex("no-www", @"^https://www\.(.*)", "https://$1", permanent: true);
        traefik.AddHeaders("cors", headers =>
        {
            headers.AccessControlAllowOriginList.Add("https://app.example.com");
            headers.AccessControlAllowMethods.Add("GET");
            headers.AccessControlAllowMethods.Add("POST");
            headers.AccessControlMaxAge = 600;
            headers.CustomRequestHeaders["X-Forwarded-Proto"] = "https";
            headers.CustomResponseHeaders["Server"] = "";
        });
        traefik.AddSecurityHeaders("security", headers => headers.ContentSecurityPolicy = "default-src 'self'");
        traefik.AddCompress("compress");
        traefik.AddRetry("retry", attempts: 3, initialInterval: TimeSpan.FromMilliseconds(250));
        traefik.AddCircuitBreaker("breaker", "NetworkErrorRatio() > 0.30");
        traefik.AddBuffering("upload-limit", maxRequestBodyBytes: 10_485_760);
        traefik.AddInFlightRequestLimit("in-flight", 50);
        traefik.AddRateLimit("rate", average: 100, burst: 50, period: TimeSpan.FromMinutes(1));
        traefik.AddIpAllowList("office", "10.0.0.0/8", "192.168.1.7");
        traefik.AddBasicAuth("auth", htpasswd);
        traefik.AddBasicAuth("dev-auth", "admin", password);

        using var app = builder.Build();

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task RunModeDynamicConfigurationRendersRulesChainsAndStrippedPrefixes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var domain = builder.AddParameter("domain", "dev.localhost");
        var traefik = builder.AddTraefik("traefik");

        var security = traefik.AddSecurityHeaders("security");
        var compress = traefik.AddCompress("compress");
        var defaults = traefik.AddChain("defaults", security, compress);

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.{domain}");
                route.PathPrefix = "/v1";
                route.StripPathPrefix = true;
                route.Methods.Add("GET");
                route.Methods.Add("HEAD");
                route.Middlewares.Add(defaults.Resource);
            })
            .WithTraefikRoute(traefik, route =>
            {
                route.RouterName = "api-admin";
                route.Host = ReferenceExpression.Create($"admin.{domain}");
                route.Headers["X-Admin"] = "1";
                route.Query["debug"] = "true";
                route.ClientIps.Add("10.0.0.0/8");
                route.Rule = (TraefikRule.PathPrefix("/admin") | TraefikRule.PathRegexp("^/ops/[0-9]{2,3}")) & !TraefikRule.Method("DELETE");
            })
            .WithTraefikRoute(traefik, route =>
            {
                route.RouterName = "api-raw";
                route.RawRule = "Host(`a.example.com`) || Host(`b.example.com`)";
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 50001, containerHost: "api", containerPort: 8080);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Theory]
    [InlineData(1, "Host(`a`) && (PathPrefix(`/x`) || PathPrefix(`/y`))")]
    [InlineData(2, "(Host(`a`) && PathPrefix(`/x`)) || Host(`b`)")]
    [InlineData(3, "Host(`a`) && Method(`GET`) && Method(`POST`)")]
    [InlineData(4, "!(Host(`a`) || Host(`b`))")]
    [InlineData(5, "!Host(`a`) && HeaderRegexp(`X-Id`, `^[0-9]+$`) && QueryRegexp(`q`, `.+`)")]
    public async Task RulesRenderWithExplicitParentheses(int scenario, string expected)
    {
        var rule = scenario switch
        {
            1 => TraefikRule.Host("a") & (TraefikRule.PathPrefix("/x") | TraefikRule.PathPrefix("/y")),
            2 => (TraefikRule.Host("a") & TraefikRule.PathPrefix("/x")) | TraefikRule.Host("b"),
            3 => TraefikRule.Host("a").And(TraefikRule.Method("GET")).And(TraefikRule.Method("POST")),
            4 => (TraefikRule.Host("a") | TraefikRule.Host("b")).Not(),
            _ => !TraefikRule.Host("a") & TraefikRule.HeaderRegexp("X-Id", "^[0-9]+$") & TraefikRule.QueryRegexp("q", ".+"),
        };

        var expression = TraefikRouteRule.Build(new TraefikRouteOptions { Rule = rule });

        Assert.Equal(expected, await expression.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public void RawRuleCombinedWithMatchersThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<ArgumentException>(() => api.WithTraefikRoute(traefik, route =>
        {
            route.PathPrefix = "/api";
            route.RawRule = "Host(`api.dev.localhost`)";
        }));
        Assert.Equal("RawRule can't be combined with other matchers. Write the whole rule in it. (Parameter 'configure')", exception.Message);
    }

    [Fact]
    public void RuleValuesWithBackticksThrow()
    {
        var exception = Assert.Throws<ArgumentException>(() => TraefikRule.Header("X-Test", "a`b"));
        Assert.Equal("The value 'a`b' of the Header matcher can't contain '`'.", exception.Message);
    }

    [Fact]
    public void StripPathPrefixWithoutPathPrefixThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<ArgumentException>(() => api.WithTraefikRoute(traefik, route =>
        {
            route.Host = ReferenceExpression.Create($"api.dev.localhost");
            route.StripPathPrefix = true;
        }));
        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void StripPathPrefixNameCollisionThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        traefik.AddCompress("api-strip");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.PathPrefix = "/api";
                route.StripPathPrefix = true;
            });

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("The Traefik route 'api' of resource 'api' strips its path prefix with a middleware named 'api-strip', but 'traefik' already has a middleware with that name.", exception.Message);
    }

    [Fact]
    public void ChainWithMiddlewareOfAnotherTraefikThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var compress = builder.AddTraefik("other").AddCompress("compress");

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddChain("defaults", compress));
        Assert.Equal("The Traefik middleware 'compress' belongs to 'other', not 'traefik'. (Parameter 'middlewares')", exception.Message);
    }

    [Fact]
    public void BasicAuthWithPlainPasswordThrowsAtPublishTime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var password = builder.AddParameter("password", secret: true);
        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<InvalidOperationException>(() => traefik.AddBasicAuth("auth", "admin", password));
        Assert.Equal("The Traefik middleware 'auth' uses a plain password, which can't be hashed when publishing. Pass htpasswd entries to AddBasicAuth instead.", exception.Message);
    }

    [Theory]
    [InlineData("api", "The path 'api' must start with '/'. (Parameter 'prefixes')")]
    [InlineData("", "At least one value is required, and values can't be empty. (Parameter 'prefixes')")]
    public void PathMiddlewaresRejectRelativeOrEmptyPaths(string path, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddStripPrefix("strip", path));
        Assert.Equal(message, exception.Message);
    }
}
