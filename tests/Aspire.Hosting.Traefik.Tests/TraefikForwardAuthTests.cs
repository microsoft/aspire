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

public class TraefikForwardAuthTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDynamicConfigurationIncludesForwardAuthAndSignInRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");

        // A host process as the authentication server, reached from the Traefik container.
        var authServer = builder.AddExecutable("auth-server", "dotnet", ".")
            .WithHttpEndpoint(port: 5300);
        var check = traefik.AddForwardAuth("check", authServer.GetEndpoint("http"), "/verify",
            options => options.AuthResponseHeaders.Add("X-User"));

        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret);

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost", check);

        var admin = builder.AddContainer("admin", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "admin.dev.localhost")
            .WithTraefikMiddleware(sso);

        using var app = builder.Build();

        EndpointAllocation.Allocate(authServer.Resource, "http", hostPort: 5300, containerHost: "host.docker.internal", containerPort: 5300);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 50001, containerHost: "api", containerPort: 8080);
        EndpointAllocation.Allocate(admin.Resource, "http", hostPort: 50002, containerHost: "admin", containerPort: 8080);
        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        EndpointAllocation.Allocate(proxy, "http", hostPort: 50003, containerHost: "sso-proxy", containerPort: 4180);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task OAuth2ProxyArgumentsForKeycloak()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");

        // Stands in for the Keycloak resource, which this package doesn't reference.
        var keycloak = builder.AddContainer("keycloak", "keycloak")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "auth.dev.localhost");

        var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, realm: "demo", clientId: "oauth2-proxy", clientSecret, options =>
        {
            options.AllowedGroups.Add("admins");
            options.AllowUnverifiedEmail = true;
        });

        builder.AddContainer("admin", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "admin.dev.localhost")
            .WithTraefikRoute(traefik, "admin2.dev.localhost")
            .WithTraefikMiddleware(sso);

        using var app = builder.Build();

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);
        EndpointAllocation.Allocate(keycloak.Resource, "http", hostPort: 50004, containerHost: "keycloak", containerPort: 8080);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        var args = await ArgumentEvaluator.GetArgumentListAsync(proxy, app.Services);
        var keycloakEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(keycloak.Resource, DistributedApplicationOperation.Run, app.Services);

        await Verify(new
        {
            Args = args,
            KeycloakHostname = keycloakEnv["KC_HOSTNAME"],
            KeycloakBackchannel = keycloakEnv["KC_HOSTNAME_BACKCHANNEL_DYNAMIC"],
            KeycloakProxyHeaders = keycloakEnv["KC_PROXY_HEADERS"],
        });
    }

    [Theory]
    [InlineData(null, "--cookie-secure=true")]
    [InlineData("web", "--cookie-secure=false")]
    public async Task OAuth2ProxyCookieIsSecureOnlyWhenEveryProtectedRouteUsesHttps(string? plainEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client", builder.AddParameter("client-secret", "secret", secret: true));

        builder.AddContainer("app", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"secure.dev.localhost");
                route.Middlewares.Add(sso.Resource);
            })
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"plain.dev.localhost");
                route.EntryPoint = plainEntryPoint;
                route.Middlewares.Add(sso.Resource);
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);
        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        var args = await ArgumentEvaluator.GetArgumentListAsync(proxy, app.Services);

        Assert.Single(args, a => a.StartsWith("--cookie-secure=", StringComparison.Ordinal));
        Assert.Contains(expected, args);
    }

    [Fact]
    public void HostRoutedOverHttpAndHttpsGetsSignInRouteOnEachEntryPoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client", builder.AddParameter("client-secret", "secret", secret: true));

        // The callback comes back on the entry point the user signed in from. The third route names `websecure`, which is
        // already the default entry point of the second, so it needs no route of its own.
        builder.AddContainer("app", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.EntryPoint = TraefikResource.WebEntryPointName;
                route.Middlewares.Add(sso.Resource);
            })
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.Middlewares.Add(sso.Resource);
            })
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.PathPrefix = "/admin";
                route.EntryPoint = TraefikResource.WebSecureEntryPointName;
                route.Middlewares.Add(sso.Resource);
            });

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        Assert.Collection(
            proxy.Annotations.OfType<TraefikRouteAnnotation>().Where(a => a.IsSignInRoute),
            web => Assert.Equal(("sso-proxy-1", "web"), (web.Options.RouterName, web.Options.EntryPoint)),
            websecure => Assert.Equal(("sso-proxy-2", (string?)null), (websecure.Options.RouterName, websecure.Options.EntryPoint)));
        Assert.Single(sso.Resource.ProtectedHosts);
        Assert.True(sso.Resource.ProtectsPlainHttp);
    }

    [Fact]
    public void OAuth2ProxyNameClashLeavesModelUnchanged()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        builder.AddContainer("SSO-proxy", "image");
        var resourceNames = builder.Resources.Select(r => r.Name).ToList();

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client", clientSecret));
        Assert.Equal("The oauth2-proxy middleware 'sso' needs the resource name 'sso-proxy', which resource 'SSO-proxy' already has. (Parameter 'name')", exception.Message);
        Assert.Equal(resourceNames, builder.Resources.Select(r => r.Name));
        Assert.Empty(traefik.Resource.Middlewares);
    }

    [Fact]
    public async Task PublishResolvesForwardAuthAddressAndJoinsExternalNetwork()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var clientSecret = builder.AddParameter("client-secret", secret: true);
        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", "websecure", "letsencrypt");

        var authServer = builder.AddContainer("auth-server", "myregistry/auth")
            .WithHttpEndpoint(targetPort: 9000);
        var check = traefik.AddForwardAuth("check", authServer.GetEndpoint("http"), "/verify");

        var keycloak = builder.AddContainer("keycloak", "keycloak")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "auth.example.com");
        var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, "demo", "oauth2-proxy", clientSecret);

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com", check)
            .WithTraefikRoute(traefik, "admin.example.com", sso);

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public async Task OAuth2ProxyOptionsSetPrefixCookieAndMoreArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client", builder.AddParameter("client-secret", "secret", secret: true), options =>
        {
            options.ProxyPrefix = "/sso";
            options.CookieSecure = true;
            options.CookieName = "_app_session";
            options.CookieDomains.Add(".dev.localhost");
            options.AdditionalArguments.Add("--session-store-type=redis");
        });

        builder.AddContainer("app", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "app.dev.localhost")
            .WithTraefikMiddleware(sso);

        using var app = builder.Build();

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        var args = await ArgumentEvaluator.GetArgumentListAsync(proxy, app.Services);

        await Verify(new
        {
            Args = args,
            SignInRoutes = proxy.Annotations.OfType<TraefikRouteAnnotation>().Select(a => a.Options.PathPrefix),
        });
    }

    [Theory]
    [InlineData("sso", "The proxy prefix 'sso' must start with '/'. (Parameter 'configure')")]
    [InlineData("/", "The proxy prefix '/' can't end with '/' or contain '`'. (Parameter 'configure')")]
    [InlineData("/sso/", "The proxy prefix '/sso/' can't end with '/' or contain '`'. (Parameter 'configure')")]
    public void OAuth2ProxyWithInvalidProxyPrefixThrows(string proxyPrefix, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret,
            options => options.ProxyPrefix = proxyPrefix));
        Assert.Equal(message, exception.Message);
    }

    [Theory]
    [InlineData(null, "--whitelist-domain=app.example.com")]
    [InlineData(8443, "--whitelist-domain=app.example.com:*")]
    public async Task OAuth2ProxyAllowsRedirectsToNonStandardPublishedPort(int? port, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", "https")
            .WithExternalEntryPoint("https", isTls: true, port);
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client", builder.AddParameter("client-secret", "secret", secret: true));

        builder.AddContainer("app", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "app.example.com")
            .WithTraefikMiddleware(sso);

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: true);

        var proxy = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "sso-proxy");
        var args = await ArgumentEvaluator.GetArgumentListAsync(proxy, DistributedApplicationOperation.Publish, app.Services);

        Assert.Equal(expected, Assert.Single(args, a => a.StartsWith("--whitelist-domain=", StringComparison.Ordinal)));
    }

    [Fact]
    public void OAuth2ProxyRouteWithoutHostThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret);

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.PathPrefix = "/admin";
                route.Middlewares.Add(sso.Resource);
            });

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("A Traefik route of resource 'api' uses the oauth2-proxy middleware 'sso', but has no host. The sign-in callback needs one.", exception.Message);
    }

    [Fact]
    public void OAuth2ProxyWithPartialEndpointsThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "web", clientSecret,
            options => options.LoginUrl = ReferenceExpression.Create($"https://login.example.com/authorize")));
        Assert.Equal("Set LoginUrl, RedeemUrl, and JwksUrl together, or none of them. (Parameter 'configure')", exception.Message);
    }

    [Fact]
    public void KeycloakForwardAuthWithoutRouteThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var clientSecret = builder.AddParameter("client-secret", "secret", secret: true);
        var traefik = builder.AddTraefik("traefik");
        var keycloak = builder.AddContainer("keycloak", "keycloak").WithHttpEndpoint(targetPort: 8080);

        var exception = Assert.Throws<InvalidOperationException>(() => traefik.AddKeycloakForwardAuth("sso", keycloak, "demo", "oauth2-proxy", clientSecret));
        Assert.Equal("Keycloak resource 'keycloak' has no Traefik route, so it has no public URL to sign in on. Call WithTraefikRoute on it first.", exception.Message);
    }

    [Fact]
    public async Task ErrorPagesUseTheRouteOfTheErrorPageResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var pages = builder.AddContainer("pages", "nginx")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"errors.dev.localhost");
                route.RouterName = "error-pages";
            });
        var errors = traefik.AddErrorPages("errors", ["500-599", "404"], pages, "/{status}.html");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost", errors);

        using var app = builder.Build();

        EndpointAllocation.Allocate(pages.Resource, "http", hostPort: 50005, containerHost: "pages", containerPort: 80);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 50006, containerHost: "api", containerPort: 8080);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public void ErrorPagesWithoutRouteThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var pages = builder.AddContainer("pages", "nginx").WithHttpEndpoint(targetPort: 80);
        var errors = traefik.AddErrorPages("errors", ["404"], pages);

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost", errors);

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("The Traefik middleware 'errors' serves error pages from 'pages', which has no route through 'traefik'. Call WithTraefikRoute on it.", exception.Message);
    }
}
