// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesIngressTests
{
    [Fact]
    public async Task AddIngress_WithRoute_GeneratesIngressYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public")
            .WithIngressClass("nginx");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithRoute("/api", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        // Verify ingress YAML was generated
        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("Ingress", content);
        Assert.Contains("ingressClassName", content);
        Assert.Contains("nginx", content);
        Assert.Contains("path: \"/api\"", content);
        Assert.Contains("pathType", content);
        Assert.Contains("Prefix", content);
    }

    [Fact]
    public async Task AddIngress_WithHostRoute_GeneratesHostRule()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithRoute("api.example.com", "/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("api.example.com", content);
        Assert.Contains("path: \"/\"", content);
    }

    [Fact]
    public async Task AddIngress_WithTls_GeneratesTlsSection()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress
            .WithRoute("api.example.com", "/", api.GetEndpoint("http"))
            .WithHostname("api.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("my-tls-secret", content);
        Assert.Contains("api.example.com", content);
    }

    [Fact]
    public async Task AddIngress_WithMultipleRoutes_GroupsByHost()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        // Two routes on the same host
        ingress.WithRoute("example.com", "/api", api.GetEndpoint("http"));
        ingress.WithRoute("example.com", "/", web.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        // Should have one host rule with two paths
        Assert.Contains("example.com", content);
        Assert.Contains("/api", content);
        Assert.Contains("path: \"/\"", content);
    }

    [Fact]
    public async Task AddIngress_WithAnnotations_GeneratesAnnotations()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public")
            .WithIngressAnnotation("nginx.ingress.kubernetes.io/rewrite-target", "/$1");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithRoute("/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("nginx.ingress.kubernetes.io/rewrite-target", content);
    }

    [Fact]
    public async Task AddIngress_WithExactPathType_GeneratesExactPathType()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithRoute("/exact", api.GetEndpoint("http"), IngressPathType.Exact);

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("Exact", content);
    }

    [Fact]
    public async Task AddIngress_NoRoutes_DoesNotGenerateYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        k8s.AddIngress("empty");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        // No ingress file should exist for empty ingress
        var ingressDir = Path.Combine(tempDir.Path, "templates", "empty");
        Assert.False(Directory.Exists(ingressDir), $"Ingress directory should not exist at {ingressDir}");
    }

    [Fact]
    public async Task AddIngress_BackwardCompatible_NoIngressNoChange()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        // No ingress defined at all - should work as before
        builder.AddKubernetesEnvironment("env");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        // Service and deployment should exist but no ingress
        var templatesDir = Path.Combine(tempDir.Path, "templates", "myapi");
        Assert.True(Directory.Exists(templatesDir));

        var files = Directory.GetFiles(templatesDir);
        Assert.DoesNotContain(files, f => f.Contains("ingress", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddIngress_MultipleIngresses_GeneratesSeparateYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");

        var publicIngress = k8s.AddIngress("public-ingress")
            .WithIngressClass("nginx");

        var internalIngress = k8s.AddIngress("internal-ingress")
            .WithIngressClass("internal");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var admin = builder.AddContainer("myadmin", "nginx")
            .WithHttpEndpoint(targetPort: 9090);

        publicIngress.WithRoute("/", api.GetEndpoint("http"));
        internalIngress.WithRoute("/admin", admin.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        // Both ingresses should have their own template directories
        var publicPath = Path.Combine(tempDir.Path, "templates", "public-ingress", "public-ingress.yaml");
        var internalPath = Path.Combine(tempDir.Path, "templates", "internal-ingress", "internal-ingress.yaml");

        Assert.True(File.Exists(publicPath), $"Public ingress YAML not found at {publicPath}");
        Assert.True(File.Exists(internalPath), $"Internal ingress YAML not found at {internalPath}");

        var publicContent = await File.ReadAllTextAsync(publicPath);
        var internalContent = await File.ReadAllTextAsync(internalPath);

        Assert.Contains("nginx", publicContent);
        Assert.Contains("internal", internalContent);
    }

    [Fact]
    public async Task AddIngress_TlsWithDefaultBackend_AutoGeneratesHostRule()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        // TLS host + default backend but NO explicit route for the TLS host.
        // The ingress should auto-generate a rule for the TLS host.
        ingress
            .WithDefaultBackend(web.GetEndpoint("http"))
            .WithHostname("app.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);

        // Should have TLS config
        Assert.Contains("my-tls-secret", content);
        Assert.Contains("app.example.com", content);

        // Should have auto-generated rule for the TLS host
        Assert.Contains("Prefix", content);
    }

    [Fact]
    public async Task AddIngress_TlsWithExplicitRoute_DoesNotDuplicate()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        // Explicit route for the TLS host ΓÇö should NOT auto-generate another one
        ingress
            .WithRoute("app.example.com", "/", web.GetEndpoint("http"))
            .WithDefaultBackend(web.GetEndpoint("http"))
            .WithHostname("app.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        // Count occurrences of the host rule ΓÇö should appear exactly once
        var hostCount = content.Split("app.example.com").Length - 1;
        // TLS hosts list + one rule = 2 occurrences (not 3 which would mean duplicate rule)
        Assert.Equal(2, hostCount);
    }

    [Fact]
    public void WithRoute_InvalidPath_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        Assert.Throws<ArgumentException>(() =>
            ingress.WithRoute("no-leading-slash", api.GetEndpoint("http")));
    }

    [Fact]
    public void WithTls_NoArg_GeneratesSecretName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        ingress.WithTls();

        Assert.Single(ingress.Resource.TlsConfigs);
        Assert.Equal("public-tls", ingress.Resource.TlsConfigs[0].SecretName.Format);
    }

    [Fact]
    public void AddIngress_ResourceType_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        Assert.Equal(k8s.Resource, ingress.Resource.Parent);
        Assert.IsType<KubernetesIngressResource>(ingress.Resource);
    }
}
