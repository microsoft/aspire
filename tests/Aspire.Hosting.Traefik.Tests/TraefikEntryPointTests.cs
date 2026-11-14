// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikEntryPointTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeEntryPointMiddlewaresAndRoutedDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var htpasswd = builder.AddParameter("dashboard-htpasswd", "admin:{SHA}0DPiKuNIrrVmD8IUCuw1hQxNqZc=", secret: true);
        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var headers = traefik.AddSecurityHeaders("security-headers");
        var limit = traefik.AddRateLimit("limit", average: 100, burst: 50);
        traefik.WithEntryPointMiddleware("websecure", headers)
            .WithEntryPointMiddleware("websecure", limit, headers)
            .WithDashboard("traefik.dev.localhost", traefik.AddBasicAuth("dashboard-auth", htpasswd));

        using var app = builder.Build();

        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource);
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(new { Args = args, Yaml = yaml });
    }

    [Fact]
    public async Task PublishWritesTraefikDynamicConfigurationAsComposeConfig()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var htpasswd = builder.AddParameter("dashboard-htpasswd", secret: true);
        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        // A literal `$` must reach Traefik as one `$` after Docker Compose interpolation.
        var headers = traefik.AddHeaders("headers", headers => headers.CustomResponseHeaders["X-Price"] = "$5");
        var authServer = builder.AddContainer("auth-server", "myregistry/auth")
            .WithHttpEndpoint(targetPort: 9000);
        var check = traefik.AddForwardAuth("check", authServer.GetEndpoint("http"), "/verify");
        var defaults = traefik.AddChain("defaults", headers, check);

        traefik.WithEntryPointMiddleware("websecure", defaults)
            .WithDashboard(ReferenceExpression.Create($"traefik.{domain}"), traefik.AddBasicAuth("dashboard-auth", htpasswd));

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"));

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public async Task RoutedDashboardWithSingleSignOnGetsSignInRoute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret);
        traefik.WithDashboard("traefik.dev.localhost", sso);

        using var app = builder.Build();

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        EndpointAllocation.Allocate(proxy, "http", hostPort: 50003, containerHost: "sso-proxy", containerPort: 4180);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public void EntryPointMiddlewaresWithExternalTraefikThrowAtPublish()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddDockerComposeEnvironment("compose");

        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public");
        traefik.WithEntryPointMiddleware("websecure", traefik.AddCompress("compress"));

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: true));
        Assert.Equal("Traefik resource 'traefik' is published as external, so its entry-point middlewares can't be deployed. Configure them on the external Traefik instead.", exception.Message);
    }

    [Fact]
    public async Task EntryPointMiddlewaresOnMissingEntryPointThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        traefik.WithEntryPointMiddleware("websecure", traefik.AddCompress("compress"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' has middlewares for the entry point 'websecure', which doesn't exist. Add it with WithEntryPoint, or WithLetsEncrypt for 'websecure'.", exception.Message);
    }

    [Fact]
    public void InvalidEntryPointMiddlewaresThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");
        var other = builder.AddTraefik("other");
        var compress = traefik.AddCompress("compress");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret);

        var signIn = Assert.Throws<ArgumentException>(() => traefik.WithEntryPointMiddleware("web", traefik.AddChain("protected", compress, sso)));
        Assert.Equal("The oauth2-proxy middleware 'sso' can't be an entry-point middleware, because it would also intercept the sign-in callback. Apply it to the routes with WithTraefikMiddleware. (Parameter 'middlewares')", signIn.Message);

        var healthCheck = Assert.Throws<ArgumentException>(() => traefik.WithEntryPointMiddleware("traefik", compress));
        Assert.Equal("entryPoint", healthCheck.ParamName);

        var foreign = Assert.Throws<ArgumentException>(() => other.WithEntryPointMiddleware("web", compress));
        Assert.Equal("The Traefik middleware 'compress' belongs to 'traefik', not 'other'. (Parameter 'middlewares')", foreign.Message);

        var unprotected = Assert.Throws<ArgumentException>(() => traefik.WithDashboard("traefik.dev.localhost"));
        Assert.Equal("middlewares", unprotected.ParamName);
    }
}
