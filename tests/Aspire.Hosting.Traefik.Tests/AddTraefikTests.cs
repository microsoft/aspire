// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Traefik.Tests;

public class AddTraefikTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddTraefikAddsContainerWithEntryPoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddTraefik("traefik");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());

        Assert.True(resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var image));
        Assert.Equal(TraefikContainerImageTags.Registry, image.Registry);
        Assert.Equal(TraefikContainerImageTags.Image, image.Image);
        Assert.Equal(TraefikContainerImageTags.Tag, image.Tag);

        Assert.Collection(resource.Annotations.OfType<EndpointAnnotation>(),
            endpoint =>
            {
                Assert.Equal("web", endpoint.Name);
                Assert.Equal("http", endpoint.UriScheme);
                Assert.Equal(80, endpoint.TargetPort);
                Assert.Null(endpoint.Port);
                Assert.True(endpoint.IsExternal);
            },
            endpoint =>
            {
                Assert.Equal("traefik", endpoint.Name);
                Assert.Equal("http", endpoint.UriScheme);
                Assert.Equal(8080, endpoint.TargetPort);
                Assert.False(endpoint.IsExternal);
            });

        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("traefik_traefik_/ping_200_check", healthCheck.Key);
    }

    [Fact]
    public void AddTraefikUsesStandardPortsAtPublishTime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);

        Assert.Equal(80, endpoints["web"].Port);
        Assert.Equal(443, endpoints["websecure"].Port);
        Assert.Equal("https", endpoints["websecure"].UriScheme);

        var mount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>(), m => m.Target == "/var/run/docker.sock");
        Assert.True(mount.IsReadOnly);
    }

    [Fact]
    public void WithHostPortSetsWebAndWebSecurePorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // The HTTPS port is set before the websecure entry point exists, which must still apply.
        builder.AddTraefik("traefik")
            .WithHostPort(8000)
            .WithHostHttpsPort(8443)
            .WithLetsEncrypt("admin@example.com");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);

        Assert.Equal(8000, endpoints["web"].Port);
        Assert.Equal(8443, endpoints["websecure"].Port);
    }

    [Fact]
    public async Task RunModeArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .WithLetsEncrypt("admin@example.com")
            .WithHttpsRedirect()
            .WithEntryPoint("amqps", targetPort: 5671)
            .WithEntryPoint("admin", targetPort: 9443, scheme: "https");

        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource);

        await Verify(args);
    }

    [Theory]
    [InlineData(null, "--entrypoints.web.http.redirections.entrypoint.to=websecure")]
    [InlineData(8443, "--entrypoints.web.http.redirections.entrypoint.to=:8443")]
    public async Task HttpsRedirectAtPublishTimeUsesThePublicHttpsPort(int? httpsPort, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithHttpsRedirect()
            .WithHostHttpsPort(httpsPort);

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal(expected, Assert.Single(args, a => a.StartsWith("--entrypoints.web.http.redirections.entrypoint.to=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HttpsEntryPointUsesDefaultResolverAtPublishTime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithEntryPoint("admin", targetPort: 9443, scheme: "https");

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal(
            ["--entrypoints.admin.address=:9443", "--entrypoints.admin.http.tls.certresolver=letsencrypt"],
            args.Where(a => a.StartsWith("--entrypoints.admin.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunModeArgumentsSendTelemetryToDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["AppHost:OtlpApiKey"] = "test-api-key";

        // Stand in for the dashboard resource that `aspire run` adds.
        var dashboard = builder.AddResource(new ContainerResource("aspire-dashboard"))
            .WithEndpoint(name: "otlp-grpc", scheme: "https", targetPort: 18889);
        EndpointAllocation.Allocate(dashboard.Resource, "otlp-grpc", hostPort: 18889, containerHost: "host.docker.internal", containerPort: 18889);

        var traefik = builder.AddTraefik("traefik");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource, app.Services);

        await Verify(args);
    }

    [Fact]
    public async Task HttpsRedirectWithoutTlsThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithHttpsRedirect();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' redirects to HTTPS, but no TLS is configured. Call WithLetsEncrypt before relying on WithHttpsRedirect.", exception.Message);
    }

    [Fact]
    public async Task AcmeCaServerWithoutLetsEncryptThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithAcmeCaServer("https://acme.example.com/directory");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' sets an ACME CA server, but no certificate resolver is configured. Call WithLetsEncrypt.", exception.Message);
    }

    [Fact]
    public async Task AcmeCaServerWithStagingThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com", staging: true)
            .WithAcmeCaServer("https://acme.example.com/directory");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' sets an ACME CA server, and its resolver 'letsencrypt' sets staging: true. Use one of them.", exception.Message);
    }

    [Theory]
    [InlineData("acme.example.com/directory")]
    [InlineData("ftp://acme.example.com/directory")]
    public void WithAcmeCaServerRejectsInvalidUrls(string caServer)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.WithAcmeCaServer(caServer));
        Assert.Equal($"The ACME directory URL '{caServer}' must be an absolute HTTP or HTTPS URL. (Parameter 'caServer')", exception.Message);
    }

    [Theory]
    [InlineData("web", "The entry point name 'web' is reserved by the Traefik integration. (Parameter 'name')")]
    [InlineData("websecure", "The entry point name 'websecure' is reserved by the Traefik integration. (Parameter 'name')")]
    [InlineData("traefik", "The entry point name 'traefik' is reserved by the Traefik integration. (Parameter 'name')")]
    [InlineData("bad.name", "The entry point name 'bad.name' is invalid. Use letters, digits, '-' and '_' only. (Parameter 'name')")]
    public void WithEntryPointRejectsReservedAndInvalidNames(string name, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.WithEntryPoint(name, targetPort: 9000));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void WithEntryPointRejectsNameUsedTwice()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithEntryPoint("echo", targetPort: 9000);

        // Endpoint names are case-insensitive, so a different case is the same entry point.
        var exception = Assert.Throws<ArgumentException>(() => traefik.WithEntryPoint("Echo", targetPort: 9001, scheme: "udp"));
        Assert.Equal("Traefik resource 'traefik' already has an entry point or endpoint named 'Echo'. (Parameter 'name')", exception.Message);
        Assert.Collection(traefik.Resource.CustomEntryPoints, entryPoint => Assert.Equal((9000, false), (entryPoint.TargetPort, entryPoint.IsUdp)));
    }

    [Theory]
    [InlineData(0, null, "targetPort ('0') must be greater than or equal to '1'. (Parameter 'targetPort')\nActual value was 0.")]
    [InlineData(65536, null, "targetPort ('65536') must be less than or equal to '65535'. (Parameter 'targetPort')\nActual value was 65536.")]
    [InlineData(9000, 0, "port ('0') must be greater than or equal to '1'. (Parameter 'port')\nActual value was 0.")]
    [InlineData(9000, 70000, "port ('70000') must be less than or equal to '65535'. (Parameter 'port')\nActual value was 70000.")]
    public void WithEntryPointRejectsPortsOutOfRange(int targetPort, int? port, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => traefik.WithEntryPoint("echo", targetPort, port));
        Assert.Equal(message, exception.Message.ReplaceLineEndings("\n"));
        Assert.Empty(traefik.Resource.CustomEntryPoints);
    }

    [Fact]
    public void WithDashboardAddsDashboardUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard();

        Assert.True(traefik.Resource.DashboardEnabled);
        Assert.Single(traefik.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());
    }

    [Fact]
    public async Task WithDashboardCalledAgainKeepsSingleDashboardPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard();
        var auth = traefik.AddBasicAuth("auth", "admin", builder.AddParameter("password", "admin", secret: true));
        traefik.WithDashboard("traefik.dev.localhost", auth);

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var context = new ResourceUrlsCallbackContext(executionContext, traefik.Resource,
            [new ResourceUrlAnnotation { Url = "http://localhost:8080", Endpoint = traefik.GetEndpoint("traefik") }]);
        foreach (var callback in traefik.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Collection(context.Urls,
            url =>
            {
                Assert.Equal("http://localhost:8080/dashboard/", url.Url);
                Assert.Equal("Dashboard", url.DisplayText);
            },
            url =>
            {
                Assert.Equal("http://traefik.dev.localhost:8000/dashboard/", url.Url);
                Assert.Equal("Dashboard (routed)", url.DisplayText);
            });
    }
}
