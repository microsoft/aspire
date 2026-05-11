// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Utils;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesPublisherTests()
{
    [Fact]
    public async Task PublishAsync_GeneratesValidHelmChart()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        var param0 = builder.AddParameter("param0");
        var param1 = builder.AddParameter("param1", secret: true);
        var param2 = builder.AddParameter("param2", "default", publishValueAsDefault: true);
        var param3 = builder.AddResource(ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, "param3"));
        var cs = builder.AddConnectionString("cs", ReferenceExpression.Create($"Url={param0}, Secret={param1}"));

        // Add a container to the application
        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEnvironment("param0", param0)
            .WithEnvironment("param1", param1)
            .WithEnvironment("param2", param2)
            .WithEnvironment("param3", param3)
            .WithReference(cs)
            .WithVolume("logs", "/logs")
            .WithArgs("--cs", cs.Resource);

        builder.AddProject<TestProject>("project1", launchProfileName: null)
            .WithReference(api.GetEndpoint("http"));

        var app = builder.Build();

        app.Run();

        // Assert
        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/project1/deployment.yaml",
            "templates/project1/config.yaml",
            "templates/myapp/deployment.yaml",
            "templates/myapp/service.yaml",
            "templates/myapp/config.yaml",
            "templates/myapp/secrets.yaml"
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAppliesServiceCustomizations()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .WithProperties(e => e.DefaultImagePullPolicy = "Always");

        // Add a container to the application
        var container = builder.AddContainer("service", "nginx")
            .WithEnvironment("ORIGINAL_ENV", "value")
            .PublishAsKubernetesService(serviceResource =>
            {
                serviceResource.Workload!.PodTemplate.Spec.Containers[0].ImagePullPolicy = "Always";
                (serviceResource.Workload as Deployment)!.Spec.RevisionHistoryLimit = 5;
            });

        var app = builder.Build();

        app.Run();

        // Assert
        var deploymentPath = Path.Combine(tempDir.Path, "templates/service/deployment.yaml");
        Assert.True(File.Exists(deploymentPath));

        var content = await File.ReadAllTextAsync(deploymentPath);

        await Verify(content, "yaml");
    }

    [Fact]
    public async Task PublishAsync_CustomWorkloadAndResourceType()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        // Add a container to the application
        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithHttpEndpoint(targetPort: 8080)
            .PublishAsKubernetesService(serviceResource =>
            {
                serviceResource.Workload = new ArgoRollout
                {
                    Metadata = { Name = "myapp-rollout", Labels = serviceResource.Labels.ToDictionary() },
                    Spec = { Template = serviceResource.Workload!.PodTemplate, Selector = { MatchLabels = serviceResource.Labels.ToDictionary() } }
                };
                serviceResource.AdditionalResources.Add(new KedaScaledObject
                {
                    Metadata = { Name = "myapp-scaler" },
                    Spec = { ScaleTargetRef = { Kind = serviceResource.Workload.Kind!, Name = serviceResource.Workload.Metadata.Name }, MaxReplicaCount = 3 }
                });
            });

        builder.AddProject<TestProject>("project1", launchProfileName: null)
            .WithReference(api.GetEndpoint("http"));

        var app = builder.Build();

        app.Run();

        // Assert
        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/myapp/rollout.yaml",
            "templates/myapp/service.yaml",
            "templates/myapp/config.yaml",
            "templates/myapp/scaler.yaml"
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_CustomManifestResource()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .PublishAsKubernetesService(serviceResource =>
            {
                serviceResource.AddManifest("keda.sh/v1alpha1", "ScaledObject", "myapp-scaler", manifest =>
                {
                    manifest.WithNamespace("autoscaling")
                        .WithLabel("example.com/custom", "true")
                        .WithAnnotation("example.com/source", "polyglot")
                        .WithField("spec.scaleTargetRef.kind", "Deployment")
                        .WithField("spec.scaleTargetRef.name", "myapp")
                        .WithField("spec.minReplicaCount", 1)
                        .WithField("spec.maxReplicaCount", (double)3)
                        .WithField("data.enabled", true);
                });
            });

        var app = builder.Build();

        app.Run();

        var manifestPath = Path.Combine(tempDir.Path, "templates/myapp/scaler.yaml");
        Assert.True(File.Exists(manifestPath), $"Manifest should exist at {manifestPath}");

        var content = await File.ReadAllTextAsync(manifestPath);

        Assert.Contains("apiVersion: keda.sh/v1alpha1", content);
        Assert.Contains("kind: ScaledObject", content);
        Assert.Contains("myapp-scaler", content);
        Assert.Contains("namespace: \"autoscaling\"", content);
        Assert.Contains("example.com/custom: \"true\"", content);
        Assert.Contains("app.kubernetes.io/name:", content);
        Assert.Contains("example.com/source: \"polyglot\"", content);
        Assert.Contains("scaleTargetRef:", content);
        Assert.Contains("kind: \"Deployment\"", content);
        Assert.Contains("name: \"myapp\"", content);
        Assert.Contains("minReplicaCount: 1", content);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var spec = Assert.IsType<YamlMappingNode>(root.Children.Single(static entry => entry.Key is YamlScalarNode { Value: "spec" }).Value);
        var maxReplicaCount = Assert.IsType<YamlScalarNode>(spec.Children.Single(static entry => entry.Key is YamlScalarNode { Value: "maxReplicaCount" }).Value);
        Assert.Equal("3", maxReplicaCount.Value);
        Assert.DoesNotContain("maxReplicaCount: 3.0", content);
        Assert.Contains("enabled: true", content);
    }

    [Fact]
    public void KubernetesManifestResourceWithFieldThrowsWhenIntermediatePathIsScalar()
    {
        var manifest = new KubernetesManifestResource("example.com/v1", "Example", "example");
        manifest.WithField("spec", "scalar");

        var exception = Assert.Throws<ArgumentException>(() => manifest.WithField("spec.replicas", 3));

        Assert.Contains("Cannot set nested manifest field 'spec.replicas' because 'spec' already has a scalar value.", exception.Message);
    }

    [Fact]
    public async Task PublishAsync_HandlesSpecialResourceName()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
                   .WithHelm(helm => helm.WithChartName("my-chart"));

        var param0 = builder.AddParameter("param0");
        var param1 = builder.AddParameter("param1", secret: true);
        var cs = builder.AddConnectionString("api-cs", ReferenceExpression.Create($"Url={param0}, Secret={param1}"));
        var csPlain = builder.AddConnectionString("api-cs2", ReferenceExpression.Create($"host.local:80"));

        var param3 = builder.AddResource(ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, "param3"));
        builder.AddProject<TestProject>("SpeciaL-ApP", launchProfileName: null)
            .WithEnvironment("param3", param3)
            .WithReference(cs)
            .WithReference(csPlain);

        var app = builder.Build();

        app.Run();

        // Assert
        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/SpeciaL-ApP/deployment.yaml",
            "templates/SpeciaL-ApP/config.yaml",
            "templates/SpeciaL-ApP/secrets.yaml"
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_ResourceWithProbes()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        // Add a container to the application
#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var api = builder
            .AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithHttpEndpoint(targetPort: 8080)
            .WithHttpProbe(ProbeType.Readiness, "/ready")
            .WithHttpProbe(ProbeType.Liveness, "/health");

        builder
            .AddProject<TestProject>("project1", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithHttpProbe(ProbeType.Readiness,"/ready", initialDelaySeconds: 60)
            .WithHttpProbe(ProbeType.Liveness, "/health");
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        var app = builder.Build();

        app.Run();

        // Assert
        var expectedFiles = new[]
        {
            "templates/env-dashboard/deployment.yaml",
            "templates/myapp/deployment.yaml",
            "templates/project1/deployment.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_WithDockerfileFactory_WritesDockerfileToOutputFolder()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        var dockerfileContent = "FROM alpine:latest\nRUN echo 'Generated for kubernetes'";
        var container = builder.AddContainer("testcontainer", "testimage")
                               .WithDockerfileFactory(".", context => dockerfileContent);

        var app = builder.Build();
        app.Run();

        // Verify Dockerfile was written to resource-specific path
        var dockerfilePath = Path.Combine(tempDir.Path, "testcontainer.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");
        var actualContent = await File.ReadAllTextAsync(dockerfilePath);

        await Verify(actualContent);
    }

    private sealed class KedaScaledObject() : BaseKubernetesResource("keda.sh/v1alpha1", "ScaledObject")
    {
        [YamlMember(Alias = "spec")]
        public KedaScaledObjectSpec Spec { get; set; } = new();

        public sealed class KedaScaledObjectSpec
        {
            [YamlMember(Alias = "scaleTargetRef")]
            public ScaleTargetRefSpec ScaleTargetRef { get; set; } = new();

            [YamlMember(Alias = "minReplicaCount")]
            public int MinReplicaCount { get; set; } = 1;

            [YamlMember(Alias = "maxReplicaCount")]
            public int MaxReplicaCount { get; set; } = 1;

            public sealed class ScaleTargetRefSpec
            {
                [YamlMember(Alias = "name")]
                public string Name { get; set; } = null!;
                [YamlMember(Alias = "kind")]
                public string Kind { get; set; } = "Deployment";
            }

            // Omitted other properties for brevity
        }
    }

    private sealed class ArgoRollout() : Workload("argoproj.io/v1alpha1", "Rollout")
    {
        public ArgoRolloutSpec Spec { get; set; } = new();

        public sealed class ArgoRolloutSpec
        {
            [YamlMember(Alias = "replicas")]
            public int Replicas { get; set; } = 1;

            [YamlMember(Alias = "template")]
            public PodTemplateSpecV1 Template { get; set; } = new();

            [YamlMember(Alias = "selector")]
            public LabelSelectorV1 Selector { get; set; } = new();

            // Omitted other properties for brevity
        }

        [YamlIgnore]
        public override PodTemplateSpecV1 PodTemplate => Spec.Template;
    }

    [Fact]
    public async Task KubernetesWithProjectResources()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        // Add a project with multiple endpoint combinations
        var project = builder.AddProject<TestProjectWithLaunchSettings>("project1")
            .WithHttpEndpoint(name: "custom1") // port = null, targetPort = null
            .WithHttpEndpoint(port: 7001, name: "custom2") // port = 7001, targetPort = null
            .WithHttpEndpoint(targetPort: 7002, name: "custom3") // port = null, targetPort = 7002
            .WithHttpEndpoint(port: 7003, targetPort: 7004, name: "custom4"); // port = 7003, targetPort = 7004

        builder.AddContainer("api", "reg:api")
               .WithReference(project);

        var app = builder.Build();

        app.Run();

        // Assert
        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/project1/deployment.yaml",
            "templates/project1/service.yaml",
            "templates/project1/config.yaml",
            "templates/api/deployment.yaml",
            "templates/api/config.yaml"
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task KubernetesMapsPortsForBaitAndSwitchResources()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.AddKubernetesEnvironment("env");
        var api = builder.AddExecutable("api", "node", ".")
            .PublishAsDockerFile()
            .WithHttpEndpoint(env: "PORT");
        builder.AddContainer("gateway", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithReference(api.GetEndpoint("http"));
        var app = builder.Build();
        app.Run();
        // Assert
        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/api/deployment.yaml",
            "templates/api/service.yaml",
            "templates/api/config.yaml",
            "templates/gateway/deployment.yaml",
            "templates/gateway/config.yaml"
        };
        SettingsTask settingsTask = default!;
        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];
            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }
        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_HandlesConditionalReferenceExpression()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment(context =>
            {
                var conditional = ReferenceExpression.CreateConditional(
                    new TestConditionProvider(bool.TrueString),
                    bool.TrueString,
                    ReferenceExpression.Create($",ssl=true"),
                    ReferenceExpression.Empty);

                context.EnvironmentVariables["TLS_SUFFIX"] = conditional;

                var conditionalFalse = ReferenceExpression.CreateConditional(
                    new TestConditionProvider(bool.FalseString),
                    bool.TrueString,
                    ReferenceExpression.Create($",ssl=true"),
                    ReferenceExpression.Create($",ssl=false"));

                context.EnvironmentVariables["TLS_SUFFIX_FALSE"] = conditionalFalse;
            });

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/myapp/deployment.yaml",
            "templates/myapp/config.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_HandlesConditionalReferenceExpressionWithParameterCondition()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        // Use a real ParameterResource as the condition with a known default value.
        var enableTls = builder.AddParameter("enable-tls", "True", publishValueAsDefault: true);

        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment(context =>
            {
                var conditional = ReferenceExpression.CreateConditional(
                    enableTls.Resource,
                    bool.TrueString,
                    ReferenceExpression.Create($",ssl=true"),
                    ReferenceExpression.Create($",ssl=false"));

                context.EnvironmentVariables["TLS_SUFFIX"] = conditional;
            });

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/myapp/deployment.yaml",
            "templates/myapp/config.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_ConditionalWithParameterBranch_UsesIfElseSyntax()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        // The condition is a ParameterResource, and one branch also references a parameter.
        // This uses {{ if eq ... }}...{{ else }}...{{ end }} syntax since ternary arguments
        // can't contain nested Helm expressions.
        var enableTls = builder.AddParameter("enable-tls", "True", publishValueAsDefault: true);
        var tlsSuffix = builder.AddParameter("tls-suffix", ",ssl=true", publishValueAsDefault: true);

        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment(context =>
            {
                var conditional = ReferenceExpression.CreateConditional(
                    enableTls.Resource,
                    bool.TrueString,
                    ReferenceExpression.Create($"{tlsSuffix.Resource}"),
                    ReferenceExpression.Create($",ssl=false"));

                context.EnvironmentVariables["TLS_SUFFIX"] = conditional;
            });

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/env-dashboard/deployment.yaml",
            "templates/env-dashboard/service.yaml",
            "templates/myapp/deployment.yaml",
            "templates/myapp/config.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_HandlesScalarEnvironmentVariableTypes()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env");

        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["BOOL_TRUE"] = true;
                context.EnvironmentVariables["BOOL_FALSE"] = false;
                context.EnvironmentVariables["INT_VALUE"] = 42;
                context.EnvironmentVariables["DOUBLE_VALUE"] = 3.14;
                context.EnvironmentVariables["DATETIMEOFFSET_VALUE"] = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
                context.EnvironmentVariables["TIMESPAN_VALUE"] = TimeSpan.FromMinutes(90);
                context.EnvironmentVariables["URI_VALUE"] = new Uri("https://example.com/path");
                int? nullableIntWithValue = 7;
                context.EnvironmentVariables["NULLABLE_INT_WITH_VALUE"] = nullableIntWithValue!;
            })
            .WithEnvironment("STRING_VALUE", "hello");

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "Chart.yaml",
            "values.yaml",
            "templates/myapp/deployment.yaml",
            "templates/myapp/config.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            var fileExtension = Path.GetExtension(filePath)[1..];

            if (settingsTask is null)
            {
                settingsTask = Verify(File.ReadAllText(filePath), fileExtension);
            }
            else
            {
                settingsTask = settingsTask.AppendContentAsFile(File.ReadAllText(filePath), fileExtension);
            }
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_WithPvcStorageType_GeneratesValidPersistentVolumeAndClaim()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/14096 (PV emits empty
        // claimRef/hostPath/local/nodeAffinity blocks) and
        // https://github.com/microsoft/aspire/issues/16504 (PVC emits empty
        // dataSource/dataSourceRef/selector blocks). Both are caused by eager `= new()`
        // initialization of complex sub-properties on V1 spec types; once those properties are
        // nullable the YAML serializer's OmitNull configuration suppresses the empty mappings.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .WithProperties(env =>
            {
                env.DefaultStorageType = "pvc";
                env.DefaultStorageClassName = "managed-csi";
                env.DefaultStorageSize = "10Gi";
                env.DefaultStorageReadWritePolicy = "ReadWriteOnce";
            });

        builder.AddContainer("service", "nginx")
            .WithVolume("data", "/var/lib/data");

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "templates/service/deployment.yaml",
            "templates/service/data-pv.yaml",
            "templates/service/data-pvc.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            Assert.True(File.Exists(filePath), $"Expected publisher to emit {expectedFile}.");

            var content = await File.ReadAllTextAsync(filePath);

            // Empty `{ }` mappings are the failure mode the bug fixes address; assert against
            // them explicitly so a regression is obvious without snapshot-diffing.
            Assert.DoesNotContain("{}", content);

            settingsTask = settingsTask is null
                ? Verify(content, "yaml")
                : settingsTask.AppendContentAsFile(content, "yaml");
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_WithHostPathStorageType_GeneratesValidPersistentVolumeAndClaim()
    {
        // Regression test that exercises the hostPath emission path of
        // CreatePersistentVolume in addition to the PVC path. With the bug fixes the rendered
        // PV correctly contains a populated `hostPath` mapping while still omitting the empty
        // `claimRef`/`local`/`nodeAffinity` blocks that previously broke `kubectl apply`.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .WithProperties(env =>
            {
                env.DefaultStorageType = "hostpath";
                env.DefaultStorageSize = "5Gi";
            });

        builder.AddContainer("service", "nginx")
            .WithVolume("logs", "/var/log/service");

        var app = builder.Build();
        app.Run();

        var deploymentPath = Path.Combine(tempDir.Path, "templates", "service", "deployment.yaml");
        Assert.True(File.Exists(deploymentPath));

        var deploymentContent = await File.ReadAllTextAsync(deploymentPath);
        Assert.DoesNotContain("{}", deploymentContent);

        await Verify(deploymentContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_WithFirstClassPersistentVolume_BindsByName_PromotesToStatefulSet()
    {
        // First-class persistent volumes bind to a workload by matching a
        // ContainerMountAnnotation source name. The publisher routes the pod's
        // volumes[] entry through the generated PVC and promotes the workload to a
        // StatefulSet (regardless of whether the resource implements
        // IResourceWithConnectionString).
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");

        var data = k8s.AddPersistentVolume("data")
            .WithStorageClass("managed-csi")
            .WithCapacity("20Gi")
            .WithAccessMode(PersistentVolumeAccessMode.ReadWriteOnce)
            .WithVolumeAnnotation("volume.beta.kubernetes.io/storage-provisioner", "disk.csi.azure.com");

        builder.AddContainer("service", "nginx")
            .WithVolume("data", "/var/lib/data")
            .WithPersistentVolume(data);

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "templates/service/statefulset.yaml",
            "templates/data/data.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            Assert.True(File.Exists(filePath), $"Expected publisher to emit {expectedFile}.");

            var content = await File.ReadAllTextAsync(filePath);
            AssertNoBuggyEmptyMappings(content);

            settingsTask = settingsTask is null
                ? Verify(content, "yaml")
                : settingsTask.AppendContentAsFile(content, "yaml");
        }

        // The pre-existing per-resource PVC under templates/service/ must NOT be emitted —
        // the binding consumes the volume mount and routes it to the standalone PVC instead.
        var legacyPvc = Path.Combine(tempDir.Path, "templates", "service", "data-pvc.yaml");
        Assert.False(File.Exists(legacyPvc), "Bound volumes must not also be emitted via the env-default PV/PVC path.");

        // And no Deployment manifest for the bound workload — it must promote to StatefulSet.
        var deploymentPath = Path.Combine(tempDir.Path, "templates", "service", "deployment.yaml");
        Assert.False(File.Exists(deploymentPath), "Workloads bound to a persistent volume must render as a StatefulSet, not a Deployment.");

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_WithFirstClassPersistentVolume_OnProject_BindsViaMountPathOverload()
    {
        // Closes https://github.com/dotnet/aspire/issues/9430 — projects can bind a
        // persistent volume via the (volume, mountPath) overload, which adds the
        // ContainerMountAnnotation itself.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");

        var media = k8s.AddPersistentVolume("media")
            .WithStorageClass("azurefile-csi")
            .WithCapacity("100Gi")
            .WithAccessMode(PersistentVolumeAccessMode.ReadWriteMany);

        builder.AddProject<TestProject>("api", launchProfileName: null)
            .WithPersistentVolume(media, "/srv/media");

        var app = builder.Build();
        app.Run();

        var expectedFiles = new[]
        {
            "templates/api/statefulset.yaml",
            "templates/media/media.yaml",
        };

        SettingsTask settingsTask = default!;

        foreach (var expectedFile in expectedFiles)
        {
            var filePath = Path.Combine(tempDir.Path, expectedFile);
            Assert.True(File.Exists(filePath), $"Expected publisher to emit {expectedFile}.");

            var content = await File.ReadAllTextAsync(filePath);
            AssertNoBuggyEmptyMappings(content);

            settingsTask = settingsTask is null
                ? Verify(content, "yaml")
                : settingsTask.AppendContentAsFile(content, "yaml");
        }

        await settingsTask;
    }

    [Fact]
    public async Task PublishAsync_WithFirstClassPersistentVolume_FallsThroughForUnboundVolumes()
    {
        // A workload may declare both a bound and an unbound volume. The bound one
        // routes through the standalone PVC; the unbound one falls through to the
        // env-default storage type (here: emptyDir) so existing workloads are not
        // perturbed by this feature.
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var k8s = builder.AddKubernetesEnvironment("env");

        var data = k8s.AddPersistentVolume("data")
            .WithStorageClass("managed-csi")
            .WithCapacity("5Gi");

        builder.AddContainer("service", "nginx")
            .WithVolume("data", "/var/lib/data")
            .WithVolume("scratch", "/srv/scratch")
            .WithPersistentVolume(data);

        var app = builder.Build();
        app.Run();

        var statefulSetPath = Path.Combine(tempDir.Path, "templates", "service", "statefulset.yaml");
        Assert.True(File.Exists(statefulSetPath));

        var statefulSetContent = await File.ReadAllTextAsync(statefulSetPath);
        AssertNoBuggyEmptyMappings(statefulSetContent);

        var pvcPath = Path.Combine(tempDir.Path, "templates", "data", "data.yaml");
        Assert.True(File.Exists(pvcPath));
        var pvcContent = await File.ReadAllTextAsync(pvcPath);
        Assert.DoesNotContain("{}", pvcContent);

        await Verify(statefulSetContent, "yaml")
            .AppendContentAsFile(pvcContent, "yaml");
    }

    /// <summary>
    /// Asserts that the rendered YAML does not contain known-buggy empty <c>{}</c>
    /// mappings that Kubernetes rejects on apply. Some <c>{}</c> mappings — such as
    /// <c>emptyDir: {}</c> — are valid Kubernetes shorthand and must be permitted.
    /// </summary>
    private static void AssertNoBuggyEmptyMappings(string content)
    {
        string[] buggyPatterns =
        [
            "volumeClaimRetentionPolicy: {}",
            "updateStrategy: {}",
            "rollingUpdate: {}",
            "dataSource: {}",
            "dataSourceRef: {}",
            "selector: {}",
            "claimRef: {}",
            "hostPath: {}",
            "local: {}",
            "nodeAffinity:",
        ];

        foreach (var pattern in buggyPatterns)
        {
            if (pattern == "nodeAffinity:")
            {
                // nodeAffinity is only buggy when followed by an empty `required: {}` block.
                Assert.DoesNotContain("required: {}", content);
            }
            else
            {
                Assert.DoesNotContain(pattern, content);
            }
        }
    }

    private sealed class TestConditionProvider(string value) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "test-condition";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => new(value);

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
            => new(value);
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "another-path";

        public LaunchSettings? LaunchSettings { get; set; }
    }

    private sealed class TestProjectWithLaunchSettings : IProjectMetadata
    {
        public Dictionary<string, LaunchProfile>? Profiles { get; set; } = [];
        public string ProjectPath => "another-path";
        public LaunchSettings? LaunchSettings => new() { Profiles = Profiles! };

        public TestProjectWithLaunchSettings() => Profiles = new()
        {
            ["https"] = new()
            {
                CommandName = "Project",
                LaunchBrowser = true,
                ApplicationUrl = "http://localhost:5031;https://localhost:5032",
                EnvironmentVariables = new()
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            },
            ["http"] = new()
            {
                CommandName = "Project",
                LaunchBrowser = true,
                ApplicationUrl = "http://localhost:5031",
                EnvironmentVariables = new()
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            }
        };
    }
}
