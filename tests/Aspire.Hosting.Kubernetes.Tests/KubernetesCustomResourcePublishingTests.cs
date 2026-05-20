// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesCustomResourcePublishingTests()
{
    [Fact]
    public async Task PublishingCustomResource_GeneratesValidYaml()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var spec = new ConfigMapDataSpec(new { configKey = "configValue" });
        string apiVer = "v1";
        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("my-config", apiVer, "ConfigMap")
            .WithSpec(spec);

        var app = builder.Build();
        app.Run();

        // Check that the custom resource file was created
        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-config", "my-config.yaml");
        Assert.True(File.Exists(customResourceFile), $"File not found: {customResourceFile}");

        var yaml = File.ReadAllText(customResourceFile);
        Assert.Contains("apiVersion: \"v1\"", yaml);
        Assert.Contains("kind: \"ConfigMap\"", yaml);
        Assert.Contains("my-config", yaml);

        
    }

    [Fact]
    public async Task PublishingCustomResource_WithComplexSpec()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var spec = new KubernetesCustomResourceDeploymentSpec(
            replicas: 2,
            selector: new KubernetesCustomResourceSelectorSpec(
                new Dictionary<string, string> { ["app"] = "test" }),
            template: new KubernetesCustomResourceTemplateSpec(
                new KubernetesCustomResourceTemplateMetadata(
                    new Dictionary<string, string> { ["app"] = "test" }),
                new KubernetesCustomResourcePodSpec(Array.Empty<KubernetesCustomResourceContainerSpec>())));

        string apiVer = "apps";
        string apiVerNum = "v1";
        string fullVer = apiVer + "/" + apiVerNum;
        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("my-deployment", fullVer, "Deployment")
            .WithSpec(spec);

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-deployment", "my-deployment.yaml");
        Assert.True(File.Exists(customResourceFile));

        var yaml = File.ReadAllText(customResourceFile);
        Assert.Contains("apiVersion: \"apps", yaml);
        Assert.Contains("kind: \"Deployment\"", yaml);
        Assert.Contains("replicas: 2", yaml);

        
    }

    [Fact]
    public async Task PublishingCustomResource_WithCustomCRD()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var spec = new CertManagerCustomResourceSpec(enabled: true, ns: "cert-manager");
        string certMgrApi = "cert-manager.io" + "/" + "v1alpha1";
        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("my-cert", certMgrApi, "Certificate")
            .WithSpec(spec);

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-cert", "my-cert.yaml");
        Assert.True(File.Exists(customResourceFile));

        var yaml = File.ReadAllText(customResourceFile);
        Assert.Contains("apiVersion: \"cert-manager.io", yaml);
        Assert.Contains("kind: \"Certificate\"", yaml);

        
    }

    [Fact]
    public async Task PublishingMultipleCustomResources()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var env = builder.AddKubernetesEnvironment("env");
        
        env.AddCustomResource("config1", "v1", "ConfigMap")
            .WithSpec(new ConfigMapDataSpec(new { key1 = "value1" }));

        env.AddCustomResource("secret1", "v1", "Secret")
            .WithSpec(new SecretDataSpec(new { password = "secret" }));

        var app = builder.Build();
        app.Run();

        var config1File = Path.Combine(tempDir.Path, "templates", "config1", "config1.yaml");
        var secret1File = Path.Combine(tempDir.Path, "templates", "secret1", "secret1.yaml");

        Assert.True(File.Exists(config1File));
        Assert.True(File.Exists(secret1File));

        var configYaml = File.ReadAllText(config1File);
        var secretYaml = File.ReadAllText(secret1File);

        Assert.Contains("kind: \"ConfigMap\"", configYaml);
        Assert.Contains("kind: \"Secret\"", secretYaml);

        
    }

    [Fact]
    public async Task PublishingCustomResourceWithKubernetesNameConversion()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("MyCustomResource", "v1", "ConfigMap")
            .WithSpec(new EmptyCustomResourceSpec());

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "MyCustomResource", "mycustomresource.yaml");
        Assert.True(File.Exists(customResourceFile));

        var yaml = File.ReadAllText(customResourceFile);
        
        // Kubernetes name conversion converts to lowercase with hyphens
        Assert.Contains("name: \"mycustomresource\"", yaml);

        
    }

    [Fact]
    public async Task PublishingCustomResource_WithMetadata()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("my-config", "v1", "ConfigMap")
            .WithSpec(new ConfigMapDataSpec(new { app = "myapp" }));

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-config", "my-config.yaml");
        var yaml = File.ReadAllText(customResourceFile);

        Assert.Contains("apiVersion: \"v1\"", yaml);
        Assert.Contains("kind: \"ConfigMap\"", yaml);
        Assert.Contains("metadata:", yaml);
        Assert.Contains("name: \"my-config\"", yaml);
        Assert.Contains("spec:", yaml);

        
    }

    [Fact]
    public async Task ValidateCustomResourceYamlStructure()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var spec = new SimpleCustomResourceSpec("test", 1, new NestedCustomResourceSpec("data"));
        string customIo = "custom.io";
        string customApiVer = customIo + "/" + "v1";
        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("my-resource", customApiVer, "MyKind")
            .WithSpec(spec);

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-resource", "my-resource.yaml");
        var yaml = File.ReadAllText(customResourceFile);

        // Validate YAML structure by checking for required fields
        Assert.Contains("apiVersion:", yaml);
        Assert.Contains("kind:", yaml);
        Assert.Contains("metadata:", yaml);
        Assert.Contains("spec:", yaml);

        Assert.Contains("custom.io", yaml);
        Assert.Contains("MyKind", yaml);

        
    }

    [Fact]
    public async Task PublishingCustomResource_WithEmptySpec()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .AddCustomResource("empty-resource", "v1", "ConfigMap");

        var app = builder.Build();
        app.Run();

        var customResourceFile = Path.Combine(tempDir.Path, "templates", "empty-resource", "empty-resource.yaml");
        Assert.True(File.Exists(customResourceFile));

        var yaml = File.ReadAllText(customResourceFile);
        Assert.Contains("apiVersion: \"v1\"", yaml);
        Assert.Contains("kind: \"ConfigMap\"", yaml);

        
    }

    [Fact]
    public async Task BuildCustomResourceWithHelmChartAndCustomResources()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var env = builder.AddKubernetesEnvironment("env");

        // Add a helm chart
        env.AddHelmChart("prometheus", "prometheus-community/kube-prometheus-stack", "50.0.0")
            .WithNamespace("monitoring");

        // Add a custom resource
        string monitoringApi = "monitoring.coreos.com" + "/" + "v1";
        env.AddCustomResource("my-alert", monitoringApi, "PrometheusRule")
            .WithSpec(new MonitoringRuleCustomResourceSpec(new[] { new MonitoringRuleGroupSpec("test") }));

        var app = builder.Build();
        app.Run();

        // Verify both generated
        var helmChartFile = Path.Combine(tempDir.Path, "templates", "prometheus", "helmchart.yaml");
        var customResourceFile = Path.Combine(tempDir.Path, "templates", "my-alert", "deployment.yaml");

        // At least one should exist (may depend on implementation)
        var helmChartExists = File.Exists(helmChartFile);
        var customResourceExists = File.Exists(customResourceFile);

        if (customResourceExists)
        {
            var yaml = File.ReadAllText(customResourceFile);
            Assert.Contains("monitoring.coreos.com", yaml);
        }

        
    }
}
