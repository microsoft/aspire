// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikTrafficTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeServiceOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var web = builder.AddContainer("web", "myregistry/web")
            .WithHttpEndpoint(targetPort: 8080)
            .WithHttpProbe(ProbeType.Liveness, "/alive")
            .WithHttpProbe(ProbeType.Readiness, "/ready", periodSeconds: 10, timeoutSeconds: 2)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"web.dev.localhost");
                route.PassHostHeader = false;
                route.StickySession = new TraefikStickySessionOptions
                {
                    CookieName = "affinity",
                    Secure = true,
                    SameSite = TraefikSameSiteMode.Lax,
                    MaxAgeSeconds = 3600,
                };
            });

        var api = builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithHttpProbe(ProbeType.Readiness, "/ready")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.dev.localhost");
                route.HealthCheck = new TraefikHealthCheckOptions { Path = "/healthz", IntervalSeconds = 15, Status = 204, Hostname = "api.internal" };
            });

        var grpcWeb = traefik.AddGrpcWeb("grpc-web", "https://app.example.com");
        var grpc = builder.AddContainer("grpc", "myregistry/grpc")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.Transport = "http2")
            .WithHttpProbe(ProbeType.Readiness, "/ready")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"grpc.dev.localhost");
                route.UseResourceProbe = false;
                route.Middlewares.Add(grpcWeb.Resource);
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(web.Resource, "http", hostPort: 50001, containerHost: "web", containerPort: 8080);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 50002, containerHost: "api", containerPort: 8080);
        EndpointAllocation.Allocate(grpc.Resource, "http", hostPort: 50003, containerHost: "grpc", containerPort: 8080);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task PublishServiceOptions()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("web", "myregistry/web")
            .WithHttpEndpoint(targetPort: 8080)
            .WithHttpProbe(ProbeType.Readiness, "/ready", periodSeconds: 10, timeoutSeconds: 2)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"web.example.com");
                route.PassHostHeader = false;
                // A literal `$` is escaped for Docker Compose, as in every other label.
                route.StickySession = new TraefikStickySessionOptions { CookieName = "app$affinity", HttpOnly = false };
            });

        builder.AddContainer("grpc", "myregistry/grpc")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.Transport = "http2")
            .WithTraefikRoute(traefik, "grpc.example.com");

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public async Task RunModeTcpAndUdpRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithEntryPoint("postgres", targetPort: 5432)
            .WithEntryPoint("dns", targetPort: 53, scheme: "udp");

        var db = builder.AddContainer("db", "postgres")
            .WithEndpoint(targetPort: 5432, name: "tcp")
            .WithTraefikTcpRoute(traefik, "postgres");

        var messaging = builder.AddContainer("messaging", "rabbitmq")
            .WithEndpoint(targetPort: 5672, name: "tcp")
            .WithEndpoint(targetPort: 5671, name: "amqps")
            .WithTraefikTcpRoute(traefik, route =>
            {
                route.EntryPoint = "websecure";
                route.HostSni = ReferenceExpression.Create($"amqp.dev.localhost");
                route.Tls = true;
            })
            .WithTraefikTcpRoute(traefik, route =>
            {
                route.EntryPoint = "websecure";
                route.HostSni = ReferenceExpression.Create($"amqps.dev.localhost");
                route.TlsPassthrough = true;
                route.EndpointName = "amqps";
                route.Priority = 10;
            });

        var dns = builder.AddContainer("dns", "coredns/coredns")
            .WithEndpoint(targetPort: 53, name: "udp", protocol: ProtocolType.Udp)
            .WithTraefikUdpRoute(traefik, "dns");

        using var app = builder.Build();

        EndpointAllocation.Allocate(db.Resource, "tcp", hostPort: 50001, containerHost: "db", containerPort: 5432);
        EndpointAllocation.Allocate(messaging.Resource, "tcp", hostPort: 50002, containerHost: "messaging", containerPort: 5672);
        EndpointAllocation.Allocate(messaging.Resource, "amqps", hostPort: 50003, containerHost: "messaging", containerPort: 5671);
        EndpointAllocation.Allocate(dns.Resource, "udp", hostPort: 50004, containerHost: "dns", containerPort: 53);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource);
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(new { Args = args, Yaml = yaml });
    }

    [Fact]
    public async Task PublishTcpAndUdpRoutes()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithLetsEncrypt("admin@example.com", resolverName: "le-dns")
            .WithEntryPoint("amqps", targetPort: 5671)
            .WithEntryPoint("dns", targetPort: 53, scheme: "udp")
            // Same container port as `dns`, over TCP: both mappings are kept.
            .WithEntryPoint("dns-tcp", targetPort: 53);

        builder.AddContainer("messaging", "rabbitmq")
            .WithEndpoint(targetPort: 5672, name: "tcp")
            .WithTraefikTcpRoute(traefik, route =>
            {
                route.EntryPoint = "amqps";
                route.HostSni = ReferenceExpression.Create($"amqp.{domain}");
                route.Tls = true;
            })
            .WithTraefikTcpRoute(traefik, route =>
            {
                route.EntryPoint = "websecure";
                route.HostSni = ReferenceExpression.Create($"queue.{domain}");
                route.Tls = true;
                route.CertResolver = "le-dns";
            });

        builder.AddContainer("dns", "coredns/coredns")
            .WithEndpoint(targetPort: 53, name: "udp", protocol: ProtocolType.Udp)
            .WithEndpoint(targetPort: 53, name: "tcp")
            .WithTraefikUdpRoute(traefik, "dns")
            .WithTraefikTcpRoute(traefik, "dns-tcp");

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public void InvalidTcpRouteOptionsThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var db = builder.AddContainer("db", "postgres")
            .WithEndpoint(targetPort: 5432, name: "tcp");

        var noEntryPoint = Assert.Throws<ArgumentException>(() => db.WithTraefikTcpRoute(traefik, route => route.Tls = true));
        Assert.Equal("A Traefik TCP route needs an entry point. (Parameter 'configure')", noEntryPoint.Message);

        var both = Assert.Throws<ArgumentException>(() => db.WithTraefikTcpRoute(traefik, route =>
        {
            route.EntryPoint = "websecure";
            route.Tls = true;
            route.TlsPassthrough = true;
        }));
        Assert.Equal("A Traefik TCP route either terminates TLS (Tls) or passes it through (TlsPassthrough), not both. (Parameter 'configure')", both.Message);

        var sniWithoutTls = Assert.Throws<ArgumentException>(() => db.WithTraefikTcpRoute(traefik, route =>
        {
            route.EntryPoint = "postgres";
            route.HostSni = ReferenceExpression.Create($"db.example.com");
        }));
        Assert.Equal("A Traefik TCP route can only match a server name over TLS. Set Tls or TlsPassthrough, or remove HostSni. (Parameter 'configure')", sniWithoutTls.Message);

        var resolverWithPassthrough = Assert.Throws<ArgumentException>(() => db.WithTraefikTcpRoute(traefik, route =>
        {
            route.EntryPoint = "websecure";
            route.TlsPassthrough = true;
            route.CertResolver = "letsencrypt";
        }));
        Assert.Equal("A certificate resolver needs Tls: with TLS passthrough or plain TCP, Traefik presents no certificate. (Parameter 'configure')", resolverWithPassthrough.Message);
    }

    [Fact]
    public void InvalidServiceOptionsThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var web = builder.AddContainer("web", "myregistry/web")
            .WithHttpEndpoint(targetPort: 8080);

        var path = Assert.Throws<ArgumentException>(() => web.WithTraefikRoute(traefik, route =>
        {
            route.Host = ReferenceExpression.Create($"web.example.com");
            route.HealthCheck = new TraefikHealthCheckOptions { Path = "health" };
        }));
        Assert.Equal("The health check path 'health' must start with '/'. (Parameter 'configure')", path.Message);

        var cookie = Assert.Throws<ArgumentException>(() => web.WithTraefikRoute(traefik, route =>
        {
            route.Host = ReferenceExpression.Create($"web.example.com");
            route.StickySession = new TraefikStickySessionOptions { CookieName = "my cookie" };
        }));
        Assert.Equal("The sticky session cookie name 'my cookie' is invalid. (Parameter 'configure')", cookie.Message);
    }

    [Fact]
    public void TransportRoutesNeedMatchingEntryPointsAndEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithEntryPoint("postgres", targetPort: 5432)
            .WithEntryPoint("dns", targetPort: 53, scheme: "udp");

        var missing = builder.AddContainer("missing", "postgres")
            .WithEndpoint(targetPort: 5432, name: "tcp")
            .WithTraefikTcpRoute(traefik, "mysql");
        Assert.Equal(
            "The Traefik TCP route 'missing' of resource 'missing' uses the entry point 'mysql', which 'traefik' doesn't have. Add it with WithEntryPoint, or WithLetsEncrypt for 'websecure'.",
            ApplyRoutesAndGetError(builder));
        builder.Resources.Remove(missing.Resource);

        var udpOnTcp = builder.AddContainer("udp-on-tcp", "coredns/coredns")
            .WithEndpoint(targetPort: 53, name: "udp", protocol: ProtocolType.Udp)
            .WithTraefikUdpRoute(traefik, "postgres");
        Assert.Equal(
            "The Traefik UDP route 'udp-on-tcp' of resource 'udp-on-tcp' needs a UDP entry point, but 'postgres' listens for TCP. Add one with WithEntryPoint(name, targetPort, scheme: \"udp\").",
            ApplyRoutesAndGetError(builder));
        builder.Resources.Remove(udpOnTcp.Resource);

        builder.AddContainer("tcp-endpoint", "coredns/coredns")
            .WithEndpoint(targetPort: 53, name: "dns")
            .WithTraefikUdpRoute(traefik, "dns");
        Assert.Equal(
            "The endpoint 'dns' of resource 'tcp-endpoint' uses Tcp, so it can't receive a Traefik UDP route.",
            ApplyRoutesAndGetError(builder));
    }

    [Fact]
    public void HttpRoutesNeedAnExistingTcpEntryPoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithEntryPoint("dns", targetPort: 53, scheme: "udp");

        // `websecure` only exists once TLS is configured.
        var websecure = builder.AddContainer("websecure", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"web.example.com");
                route.EntryPoint = "websecure";
            });
        Assert.Equal(
            "The Traefik HTTP route 'websecure' of resource 'websecure' uses the entry point 'websecure', which 'traefik' doesn't have. Add it with WithEntryPoint, or WithLetsEncrypt for 'websecure'.",
            ApplyRoutesAndGetError(builder));
        builder.Resources.Remove(websecure.Resource);

        builder.AddContainer("on-udp", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"web.example.com");
                route.EntryPoint = "dns";
            });
        Assert.Equal(
            "The Traefik HTTP route 'on-udp' of resource 'on-udp' needs a TCP entry point, but 'dns' listens for UDP.",
            ApplyRoutesAndGetError(builder));
    }

    private static string ApplyRoutesAndGetError(IDistributedApplicationBuilder builder)
    {
        var model = new DistributedApplicationModel(builder.Resources);
        return Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(model, isPublishMode: false)).Message;
    }
}
