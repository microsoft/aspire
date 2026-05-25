// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesIngressTests
{
    [Fact]
    public async Task AddIngress_WithPath_GeneratesIngressYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public")
            .WithIngressClass("nginx");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("/api", api.GetEndpoint("http"));

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
    public async Task AddIngress_WithHostAndPath_GeneratesHostRule()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("api.example.com", "/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("api.example.com", content);
        Assert.Contains("path: \"/\"", content);
    }

    [Fact]
    public async Task AddIngress_WithIngressClassParameter_GeneratesHelmReferenceAndPlaceholder()
    {
        // Regression test: using a ParameterResource with the WithIngressClass overload
        // previously rendered the literal format string ("{0}") into ingressClassName
        // when the parameter had no value available at publish time. The fix substitutes a
        // Helm template expression and captures the parameter so the deploy-time values
        // override file (and chart values.yaml placeholder) include the entry.
        using var tempDir = new TestTempDirectory();
        // Pipeline:ClearCache=true prevents loading of leaked deployment state from
        // ~/.aspire/deployments/<sha>/<env>.json (which would otherwise auto-resolve
        // parameters from prior test runs and bypass the MissingParameterValueException path).
        var builder = TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish",
            $"Pipeline:OutputPath={tempDir.Path}",
            "Pipeline:LogLevel=information",
            "Pipeline:Step=publish",
            "Pipeline:ClearCache=true");

        // Use a unique parameter name per test run to defeat any persistent state file lookup.
        var parameterName = $"ingressclass{Guid.NewGuid():N}";
        var ingressClass = builder.AddParameter(parameterName);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public")
            .WithIngressClass(ingressClass);

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("/api", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.DoesNotContain("\"{0}\"", content);
        Assert.Contains($"{{{{ .Values.parameters.public.{parameterName} }}}}", content);

        var valuesPath = Path.Combine(tempDir.Path, "values.yaml");
        Assert.True(File.Exists(valuesPath), $"Expected values.yaml at {valuesPath}");

        var values = await File.ReadAllTextAsync(valuesPath);
        // Expect a placeholder entry under parameters: public: <parameterName>: so consumers
        // of the published Helm chart can fill it in (and `helm template` won't substitute <no value>).
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(@"parameters:\s*[\r\n]+\s*public:\s*[\r\n]+\s*" + System.Text.RegularExpressions.Regex.Escape(parameterName) + @"\s*:"),
            values);
    }

    [Fact]
    public async Task AddIngress_WithIngressClassParameter_WithDefaultValue_ResolvesAtPublish()
    {
        // When a parameter has a publish-time default, the resolved value should be inlined
        // into the manifest rather than rendered as a Helm template reference.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var ingressClass = builder.AddParameter("ingressclass", "nginx", publishValueAsDefault: true);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public")
            .WithIngressClass(ingressClass);

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("/api", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("ingressClassName: \"nginx\"", content);
        Assert.DoesNotContain("{{ .Values", content);
    }

    [Fact]
    public async Task AddIngress_WithTls_GeneratesTlsSection()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress
            .WithPath("api.example.com", "/", api.GetEndpoint("http"))
            .WithHostname("api.example.com").WithTls("my-tls-secret");

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("my-tls-secret", content);
        Assert.Contains("api.example.com", content);
    }

    [Fact]
    public async Task AddIngress_WithTls_BeforeWithHostname_HostnameIncludedInTlsHosts()
    {
        // Regression test: WithTls() must not snapshot the hostname list at call time.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress
            .WithPath("api.example.com", "/", api.GetEndpoint("http"))
            .WithTls("my-tls-secret")
            .WithHostname("api.example.com");

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("my-tls-secret", content);
        Assert.Contains("api.example.com", content);
        // The TLS section should list the hostname under hosts.
        Assert.Matches(@"tls:[\s\S]*?hosts:[\s\S]*?api\.example\.com", content);
    }

    [Fact]
    public async Task AddIngress_WithMultiplePaths_GroupsByHost()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 80)
            .WithExternalHttpEndpoints();

        // Two paths on the same host
        ingress.WithPath("example.com", "/api", api.GetEndpoint("http"));
        ingress.WithPath("example.com", "/", web.GetEndpoint("http"));

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
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("/", api.GetEndpoint("http"));

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
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        ingress.WithPath("/exact", api.GetEndpoint("http"), IngressPathType.Exact);

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        var content = await File.ReadAllTextAsync(ingressPath);

        Assert.Contains("Exact", content);
    }

    [Fact]
    public async Task AddIngress_NoPaths_DoesNotGenerateYaml()
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
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        var admin = builder.AddContainer("myadmin", "nginx")
            .WithHttpEndpoint(targetPort: 9090)
            .WithExternalHttpEndpoints();

        publicIngress.WithPath("/", api.GetEndpoint("http"));
        internalIngress.WithPath("/admin", admin.GetEndpoint("http"));

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
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        // TLS host + default backend but NO explicit path for the TLS host.
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
    public async Task AddIngress_TlsWithExplicitPath_DoesNotDuplicate()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        // Explicit path for the TLS host -- should NOT auto-generate another one
        ingress
            .WithPath("app.example.com", "/", web.GetEndpoint("http"))
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
    public void WithPath_InvalidPath_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        Assert.Throws<ArgumentException>(() =>
            ingress.WithPath("no-leading-slash", api.GetEndpoint("http")));
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

    [Fact]
    public void AddIngress_WithPath_NonExternalEndpoint_ThrowsOnPublish()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        // Intentionally omit WithExternalHttpEndpoints to ensure the publish-time
        // validation fires and surfaces a clear, actionable message.
        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithPath("/", api.GetEndpoint("http"));

        var app = builder.Build();
        var aggregate = Assert.Throws<AggregateException>(app.Run);
        var ex = aggregate.Flatten().InnerExceptions.OfType<InvalidOperationException>().First(e => e.Message.Contains("WithExternalHttpEndpoints"));

        Assert.Contains("myapi", ex.Message);
        Assert.Contains("public", ex.Message);
        Assert.Contains("WithExternalHttpEndpoints", ex.Message);
    }

    [Fact]
    public void AddIngress_WithDefaultBackend_NonExternalEndpoint_ThrowsOnPublish()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        var web = builder.AddContainer("myweb", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithDefaultBackend(web.GetEndpoint("http"));

        var app = builder.Build();
        var aggregate = Assert.Throws<AggregateException>(app.Run);
        var ex = aggregate.Flatten().InnerExceptions.OfType<InvalidOperationException>().First(e => e.Message.Contains("WithExternalHttpEndpoints"));

        Assert.Contains("myweb", ex.Message);
        Assert.Contains("public", ex.Message);
        Assert.Contains("WithExternalHttpEndpoints", ex.Message);
    }

    [Fact]
    public async Task AddIngress_WithPath_ExternalEndpoint_Succeeds()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");
        var ingress = k8s.AddIngress("public");

        // WithExternalHttpEndpoints applied AFTER WithPath to demonstrate that
        // authoring order does not matter: validation runs at publish time.
        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithPath("/", api.GetEndpoint("http"));

        api.WithExternalHttpEndpoints();

        var app = builder.Build();
        app.Run();

        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath));
    }
}
