// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikPublishTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task OnlyResourcesDeployedWithDockerComposeGetLabelVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var compose = builder.AddDockerComposeEnvironment("compose");
        var other = builder.AddResource(new TestComputeEnvironmentResource("other"));
        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com")
            .WithComputeEnvironment(compose);

        // The routes' labels are Docker Compose customizations, which another environment ignores, but the variables
        // that carry ports into them would stay in its deployment.
        var worker = builder.AddContainer("worker", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "worker.example.com")
            .WithComputeEnvironment(other);

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: true);

        var apiEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(api.Resource, DistributedApplicationOperation.Publish);
        var workerEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal(["ASPIRE_TRAEFIK_TARGET_PORT_API"], apiEnvironment.Keys.Where(k => k.StartsWith("ASPIRE_TRAEFIK_", StringComparison.Ordinal)));
        Assert.Empty(workerEnvironment);
        Assert.Empty(worker.Resource.Annotations.OfType<DockerComposeServiceCustomizationAnnotation>());
    }

    [Fact]
    public async Task PublishWithManagedTraefikWritesLabels()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // The volume name includes a hash of the AppHost path; pin it so the snapshot is machine-independent.
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose");

        var domain = builder.AddParameter("domain");
        var email = builder.AddParameter("acme-email");

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .WithLetsEncrypt(email)
            .WithHttpsRedirect();

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        // No target port: the Docker Compose publisher assigns the container port through a `<NAME>_PORT` variable.
        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(env: "PORT")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.{domain}");
                route.PathPrefix = "/api";
                route.Priority = 100;
            });

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishedTraefikUrlFollowsTlsConfiguredAfterIt()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        // The URL is taken before TLS is configured, and still uses HTTPS in the generated environment.
        api.WithEnvironment("PUBLIC_URL", api.GetTraefikUrl());
        traefik.WithLetsEncrypt("admin@example.com");

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithParameterizedAcmeSettings()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt(ReferenceExpression.Create($"admin@{domain}"))
            .WithAcmeCaServer(caServer);

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithParameterizedExternalTraefik()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var entryPoint = builder.AddParameter("traefik-entrypoint");
        var certResolver = builder.AddParameter("traefik-certresolver");

        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", ReferenceExpression.Create($"{entryPoint}"), ReferenceExpression.Create($"{certResolver}"));

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com");

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithoutComposeDashboardOmitsTelemetry()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com");

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithExternalTraefikOmitsTraefikAndJoinsNetwork()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose");

        var domain = builder.AddParameter("domain");

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .PublishAsExternal(network: "traefik-public", entryPoint: "websecure", certResolver: "letsencrypt");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWritesMiddlewareLabelsOnServicesThatUseThem()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var origin = builder.AddParameter("cors-origin");

        var traefik = builder.AddTraefik("traefik");

        var headers = traefik.AddMiddleware("sec", "headers")
            .WithOption("frameDeny", true)
            .WithOption("stsSeconds", 31536000)
            .WithOption("accessControlAllowOriginList", [ReferenceExpression.Create($"{origin}")]);
        // `$1` is a regex group reference, which must reach Traefik as is rather than be interpolated by Compose.
        var rewrite = traefik.AddMiddleware("rewrite", "replacePathRegex")
            .WithOption("regex", "^/v1/(.*)")
            .WithOption("replacement", "/$1");
        var compress = traefik.AddMiddleware("compress", "compress");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com")
            .WithTraefikMiddleware(headers);

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.example.com");
                route.PathPrefix = "/v1";
                route.Middlewares.Add(rewrite.Resource);
            })
            .WithTraefikMiddleware(headers, compress);

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWritesChainMembersRulesAndBasicAuth()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var htpasswd = builder.AddParameter("admin-htpasswd", secret: true);

        var traefik = builder.AddTraefik("traefik");

        // The chain is the only middleware on the route, so its members must be defined on the service as well.
        var defaults = traefik.AddChain("defaults", traefik.AddSecurityHeaders("security"), traefik.AddCompress("compress"));
        var auth = traefik.AddBasicAuth("auth", htpasswd);

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.{domain}");
                route.PathPrefix = "/v1";
                route.StripPathPrefix = true;
                route.Rule = TraefikRule.PathRegexp("^/v1/[a-z]{2,8}$") | TraefikRule.Method("OPTIONS");
                route.Middlewares.Add(auth.Resource);
            })
            .WithTraefikMiddleware(defaults);

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishEscapesLiteralDollarsInArgumentsAndQuotesListValues()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // The volume name includes a hash of the AppHost path; pin it so the snapshot is machine-independent.
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var clientSecret = builder.AddParameter("client-secret", secret: true);

        // Literal `$` must stay literal in `command`, while the domain parameter must still become `${DOMAIN}`.
        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("ops$team@example.com");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.{domain}/$realm"), "client$id", clientSecret, options =>
        {
            options.AllowedGroups.Add("cn=admins,ou=groups");
            options.AllowedGroups.Add("say \"hi\"");
            options.AdditionalArguments.Add("--banner=Costs ${PRICE}");
        });

        builder.AddContainer("app", "myregistry/app")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"app.{domain}"))
            .WithTraefikMiddleware(sso);

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task RunModeKeepsLiteralDollarsInArgumentsAndQuotesListValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var sso = traefik.AddOAuth2ProxyForwardAuth("sso", ReferenceExpression.Create($"https://login.example.com"), "client$id", builder.AddParameter("client-secret", "secret", secret: true), options =>
        {
            options.AllowedGroups.Add("cn=admins,ou=groups");
            options.AllowedGroups.Add("say \"hi\"");
            options.AllowedGroups.Add("plain");
            options.AdditionalArguments.Add("--banner=Costs ${PRICE}");
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

        Assert.Equal(
            ["--client-id=client$id", "--allowed-group=\"cn=admins,ou=groups\"", "--allowed-group=\"say \"\"hi\"\"\"", "--allowed-group=plain", "--banner=Costs ${PRICE}"],
            args.Where(a => a.StartsWith("--client-id=", StringComparison.Ordinal) || a.StartsWith("--allowed-group=", StringComparison.Ordinal) || a.StartsWith("--banner=", StringComparison.Ordinal)));
    }

    [Fact]
    public void EscapeLiteralsKeepsValueProvidersAndBraces()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var domain = builder.AddParameter("domain");
        var nested = ReferenceExpression.Create($"$sub.{domain}");
        var expression = ReferenceExpression.Create($"--x={{$a}}={nested}:{domain}");

        var escaped = TraefikDockerLabels.EscapeLiterals(expression);

        Assert.Equal("--x={$$a}=$$sub.{domain.value}:{domain.value}", escaped.ValueExpression);
        Assert.Equal([nested.ValueProviders[0], domain.Resource], [((ReferenceExpression)escaped.ValueProviders[0]).ValueProviders[0], escaped.ValueProviders[1]]);
    }

    private static async Task VerifyComposeOutput(string outputPath)
    {
        var composePath = Path.Combine(outputPath, "docker-compose.yaml");
        var envPath = Path.Combine(outputPath, ".env");

        Assert.True(File.Exists(composePath));

        var verify = Verify(File.ReadAllText(composePath), "yaml");

        // The publisher only writes .env when the model has parameters.
        if (File.Exists(envPath))
        {
            verify = verify.AppendContentAsFile(File.ReadAllText(envPath), "env");
        }

        await verify;
    }
}
