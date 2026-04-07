#pragma warning disable ASPIRECOMPUTE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

public class KubernetesEnvironmentResourceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublishingKubernetesEnvironmentPublishesFile()
    {
        var tempDir = Directory.CreateTempSubdirectory(".k8s-test");
        output.WriteLine($"Temp directory: {tempDir.FullName}");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.FullName);

        builder.AddKubernetesEnvironment("env");

        // Add a container to the application
        builder.AddContainer("service", "nginx");

        var app = builder.Build();
        app.Run();

        var chartYaml = Path.Combine(tempDir.FullName, "Chart.yaml");
        var valuesYaml = Path.Combine(tempDir.FullName, "values.yaml");
        var deploymentYaml = Path.Combine(tempDir.FullName, "templates", "service", "deployment.yaml");

        await Verify(File.ReadAllText(chartYaml), "yaml")
            .AppendContentAsFile(File.ReadAllText(valuesYaml), "yaml")
            .AppendContentAsFile(File.ReadAllText(deploymentYaml), "yaml");

        tempDir.Delete(recursive: true);
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11818", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task PublishAsKubernetesService_ThrowsIfNoEnvironment()
    {
        static async Task RunTest(Action<IDistributedApplicationBuilder> action)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            // Do not add AddKubernetesEnvironment

            action(builder);

            using var app = builder.Build();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

            Assert.Contains("there are no 'KubernetesEnvironmentResource' resources", ex.Message);
        }

        await RunTest(builder =>
            builder.AddProject<Projects.ServiceA>("ServiceA")
                .PublishAsKubernetesService((_) => { }));

        await RunTest(builder =>
            builder.AddContainer("api", "myimage")
                .PublishAsKubernetesService((_) => { }));

        await RunTest(builder =>
            builder.AddExecutable("exe", "path/to/executable", ".")
                .PublishAsDockerFile()
                .PublishAsKubernetesService((_) => { }));
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11818", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task MultipleKubernetesEnvironmentsSupported()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var env1 = builder.AddKubernetesEnvironment("env1");
        var env2 = builder.AddKubernetesEnvironment("env2");

        builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithComputeEnvironment(env1);

        builder.AddProject<Projects.ServiceB>("ServiceB")
            .WithComputeEnvironment(env2);

        using var app = builder.Build();

        // Publishing will stop the app when it is done
        await app.RunAsync();

        await VerifyDirectory(tempDir.Path);
    }

    [Fact]
    public async Task GetHostAddressExpression()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddKubernetesEnvironment("env");

        var project = builder
            .AddProject<Projects.ServiceA>("project1", launchProfileName: null)
            .WithHttpEndpoint();

        var endpointReferenceEx = env.Resource.GetHostAddressExpression(project.GetEndpoint("http"));
        Assert.NotNull(endpointReferenceEx);

        Assert.Equal("project1-service", endpointReferenceEx.Format);
        Assert.Empty(endpointReferenceEx.ValueProviders);
    }

    [Fact]
    public async Task MultipleComputeEnvironmentsOnlyProcessTargetedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var kubernetes = builder.AddKubernetesEnvironment("kubernetes");
        var dockerCompose = builder.AddDockerComposeEnvironment("docker-compose");

        // Container targeted to Kubernetes
        var containerForK8s = builder.AddContainer("containerk8s", "nginx")
            .WithHttpEndpoint(port: 8080, targetPort: 80, name: "http")
            .WithComputeEnvironment(kubernetes);

        // Container targeted to Docker Compose
        var containerForDocker = builder.AddContainer("containerdocker", "nginx")
            .WithHttpEndpoint(port: 9090, targetPort: 80, name: "http")
            .WithComputeEnvironment(dockerCompose);

        // Project targeted to Kubernetes
        var projectForK8s = builder.AddProject<Projects.ServiceA>("projectk8s", launchProfileName: null)
            .WithHttpEndpoint()
            .WithComputeEnvironment(kubernetes);

        // Project targeted to Docker Compose
        var projectForDocker = builder.AddProject<Projects.ServiceA>("projectdocker", launchProfileName: null)
            .WithHttpEndpoint()
            .WithComputeEnvironment(dockerCompose);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify containerForK8s has a deployment target for Kubernetes
        var containerK8sResource = model.Resources.First(r => r.Name == "containerk8s");
        var containerK8sTarget = containerK8sResource.GetDeploymentTargetAnnotation(kubernetes.Resource);
        Assert.NotNull(containerK8sTarget);
        Assert.Same(kubernetes.Resource, containerK8sTarget.ComputeEnvironment);

        // Verify containerForDocker has a deployment target for Docker Compose
        var containerDockerResource = model.Resources.First(r => r.Name == "containerdocker");
        var containerDockerTarget = containerDockerResource.GetDeploymentTargetAnnotation(dockerCompose.Resource);
        Assert.NotNull(containerDockerTarget);
        Assert.Same(dockerCompose.Resource, containerDockerTarget.ComputeEnvironment);

        // Verify projectForK8s has a deployment target for Kubernetes
        var projectK8sResource = model.Resources.First(r => r.Name == "projectk8s");
        var projectK8sTarget = projectK8sResource.GetDeploymentTargetAnnotation(kubernetes.Resource);
        Assert.NotNull(projectK8sTarget);
        Assert.Same(kubernetes.Resource, projectK8sTarget.ComputeEnvironment);

        // Verify projectForDocker has a deployment target for Docker Compose
        var projectDockerResource = model.Resources.First(r => r.Name == "projectdocker");
        var projectDockerTarget = projectDockerResource.GetDeploymentTargetAnnotation(dockerCompose.Resource);
        Assert.NotNull(projectDockerTarget);
        Assert.Same(dockerCompose.Resource, projectDockerTarget.ComputeEnvironment);

        // Verify resources do NOT have deployment targets for other environments
        Assert.Null(containerK8sResource.GetDeploymentTargetAnnotation(dockerCompose.Resource));
        Assert.Null(containerDockerResource.GetDeploymentTargetAnnotation(kubernetes.Resource));
        Assert.Null(projectK8sResource.GetDeploymentTargetAnnotation(dockerCompose.Resource));
        Assert.Null(projectDockerResource.GetDeploymentTargetAnnotation(kubernetes.Resource));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
