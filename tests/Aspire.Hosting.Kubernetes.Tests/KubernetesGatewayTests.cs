// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesGatewayTests
{
    [Fact]
    public async Task AddGateway_WithRoute_GeneratesGatewayAndHttpRoute()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public")
            .WithGatewayClass("nginx");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        gateway.WithRoute("/api", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        // Should generate Gateway and HTTPRoute files
        var gatewayDir = Path.Combine(tempDir.Path, "templates", "public");
        Assert.True(Directory.Exists(gatewayDir), $"Gateway templates dir not found at {gatewayDir}");

        var files = Directory.GetFiles(gatewayDir);
        Assert.True(files.Length >= 2, $"Expected at least 2 files (Gateway + HTTPRoute), got {files.Length}");

        // Check Gateway YAML
        var gatewayFile = files.FirstOrDefault(f => Path.GetFileName(f) == "public.yaml");
        Assert.NotNull(gatewayFile);
        var gatewayContent = await File.ReadAllTextAsync(gatewayFile);
        Assert.Contains("Gateway", gatewayContent);
        Assert.Contains("nginx", gatewayContent);
        Assert.Contains("HTTP", gatewayContent);

        // Check HTTPRoute YAML
        var routeFile = files.FirstOrDefault(f => f.Contains("route"));
        Assert.NotNull(routeFile);
        var routeContent = await File.ReadAllTextAsync(routeFile);
        Assert.Contains("HTTPRoute", routeContent);
        Assert.Contains("/api", routeContent);
        Assert.Contains("PathPrefix", routeContent);
    }

    [Fact]
    public async Task AddGateway_WithHostRoute_GeneratesHostnameInHttpRoute()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        gateway.WithRoute("api.example.com", "/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var gatewayDir = Path.Combine(tempDir.Path, "templates", "public");
        var routeFile = Directory.GetFiles(gatewayDir).FirstOrDefault(f => f.Contains("route"));
        Assert.NotNull(routeFile);

        var content = await File.ReadAllTextAsync(routeFile);
        Assert.Contains("api.example.com", content);
        Assert.Contains("HTTPRoute", content);
    }

    [Fact]
    public async Task AddGateway_WithTls_GeneratesHttpsListener()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        gateway
            .WithRoute("api.example.com", "/", api.GetEndpoint("http"))
            .WithHostname("api.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        // Check Gateway has HTTPS listener
        var gatewayFile = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(gatewayFile);

        Assert.Contains("HTTPS", content);
        Assert.Contains("Terminate", content);
        Assert.Contains("my-tls-secret", content);
        Assert.Contains("api.example.com", content);
        // Should also have HTTP listener
        Assert.Contains("HTTP", content);
    }

    [Fact]
    public async Task AddGateway_WithTls_DoesNotDuplicateRoutes()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        gateway
            .WithRoute("api.example.com", "/", api.GetEndpoint("http"))
            .WithHostname("api.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        // Should have exactly 1 HTTPRoute file (TLS doesn't create a separate route)
        var gatewayDir = Path.Combine(tempDir.Path, "templates", "public");
        var routeFiles = Directory.GetFiles(gatewayDir).Where(f => f.Contains("route")).ToArray();
        Assert.Single(routeFiles);
    }

    [Fact]
    public async Task AddGateway_MultipleRoutes_GroupsByHost()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        // Two routes on the same host ΓåÆ should be grouped into one HTTPRoute
        gateway.WithRoute("example.com", "/api", api.GetEndpoint("http"));
        gateway.WithRoute("example.com", "/", web.GetEndpoint("http"));
        // One route on a different host
        gateway.WithRoute("other.com", "/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var gatewayDir = Path.Combine(tempDir.Path, "templates", "public");
        var routeFiles = Directory.GetFiles(gatewayDir).Where(f => f.Contains("route")).ToArray();
        // Should have 2 HTTPRoute files: one for example.com, one for other.com
        Assert.Equal(2, routeFiles.Length);
    }

    [Fact]
    public async Task AddGateway_NoRoutes_DoesNotGenerateYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        k8s.AddGateway("empty");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        var gatewayDir = Path.Combine(tempDir.Path, "templates", "empty");
        Assert.False(Directory.Exists(gatewayDir), $"Gateway directory should not exist at {gatewayDir}");
    }

    [Fact]
    public void WithRoute_InvalidPath_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        Assert.Throws<ArgumentException>(() =>
            gateway.WithRoute("no-leading-slash", api.GetEndpoint("http")));
    }

    [Fact]
    public void AddGateway_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var gateway = k8s.AddGateway("public").WithGatewayClass("test");

        Assert.Equal(k8s.Resource, gateway.Resource.Parent);
        Assert.IsType<KubernetesGatewayResource>(gateway.Resource);
    }

    [Fact]
    public async Task AddGateway_BackwardCompatible_NoGatewayNoChange()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        // Service and deployment should exist but no gateway
        var templatesDir = Path.Combine(tempDir.Path, "templates", "myapi");
        Assert.True(Directory.Exists(templatesDir));

        var files = Directory.GetFiles(templatesDir);
        Assert.DoesNotContain(files, f => f.Contains("gateway", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("route", StringComparison.OrdinalIgnoreCase));
    }
}
