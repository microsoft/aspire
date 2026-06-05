// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Docker.Tests;

public class DockerComposeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DockerComposeSetsComputeEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose");

        // Add a container to the application
        var container = builder.AddContainer("service", "nginx");

        var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Same(composeEnv.Resource, container.Resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment);
    }

    [Fact]
    public void PublishingDockerComposeEnviromentPublishesFile()
    {
        var tempDir = Directory.CreateTempSubdirectory(".docker-compose-test");
        output.WriteLine($"Temp directory: {tempDir.FullName}");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.FullName);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        // Add a container to the application
        builder.AddContainer("service", "nginx");

        var app = builder.Build();
        app.Run();

        var composeFile = Path.Combine(tempDir.FullName, "docker-compose.yaml");
        Assert.True(File.Exists(composeFile), "Docker Compose file was not created.");

        tempDir.Delete(recursive: true);
    }

    [Fact]
    public async Task DockerComposeOnlyExposesExternalEndpoints()
    {
        var tempDir = Directory.CreateTempSubdirectory(".docker-compose-test");
        output.WriteLine($"Temp directory: {tempDir.FullName}");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.FullName);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        // Add a container with both external and non-external endpoints
        builder.AddContainer("service", "nginx")
               .WithEndpoint(scheme: "http", port: 8080, name: "internal")  // Non-external endpoint
               .WithEndpoint(scheme: "http", port: 8081, name: "external", isExternal: true); // External endpoint

        var app = builder.Build();
        app.Run();

        var composeFile = Path.Combine(tempDir.FullName, "docker-compose.yaml");
        Assert.True(File.Exists(composeFile), "Docker Compose file was not created.");

        var composeContent = File.ReadAllText(composeFile);

        await Verify(composeContent, "yaml");

        tempDir.Delete(recursive: true);
    }

    [Fact]
    public async Task PublishAsDockerComposeService_ThrowsIfNoEnvironment()
    {
        static async Task RunTest(Action<IDistributedApplicationBuilder> action)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            // Do not add AddDockerComposeEnvironment

            action(builder);

            using var app = builder.Build();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

            Assert.Contains("there are no 'DockerComposeEnvironmentResource' resources", ex.Message);
        }

        await RunTest(builder =>
            builder.AddProject<Projects.ServiceA>("ServiceA")
                .PublishAsDockerComposeService((_, _) => { }));

        await RunTest(builder =>
            builder.AddContainer("api", "myimage")
                .PublishAsDockerComposeService((_, _) => { }));

        await RunTest(builder =>
            builder.AddExecutable("exe", "path/to/executable", ".")
                .PublishAsDockerFile()
                .PublishAsDockerComposeService((_, _) => { }));
    }

    [Fact]
    public async Task MultipleDockerComposeEnvironmentsSupported()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var env1 = builder.AddDockerComposeEnvironment("env1");
        var env2 = builder.AddDockerComposeEnvironment("env2");

        builder.AddContainer("api1", "myimage")
            .WithComputeEnvironment(env1);

        builder.AddContainer("api2", "myimage")
            .WithComputeEnvironment(env2);

        using var app = builder.Build();

        // Publishing will stop the app when it is done
        await app.RunAsync();

        await VerifyDirectory(tempDir.Path);
    }

    [Fact]
    public async Task DashboardWithForwardedHeadersWritesEnvVar()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("env")
               .WithDashboard(d => d.WithForwardedHeaders());

        // Add a sample service to force compose generation
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        app.Run();

        var composeFile = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composeFile), "Docker Compose file was not created.");
        var composeContent = File.ReadAllText(composeFile);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task DockerSwarmDeploymentLabelsSerializedCorrectly()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("swarm-env");

        // Add a service with Docker Swarm deployment labels
        builder.AddContainer("my-service", "my-image:latest")
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Deploy = new Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm.Deploy
                {
                    Labels = new Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm.LabelSpecs
                    {
                        ["com.example.foo"] = "bar",
                        ["com.example.env"] = "production"
                    }
                };
            });

        using var app = builder.Build();
        app.Run();

        var composeFile = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composeFile), "Docker Compose file was not created.");
        var composeContent = File.ReadAllText(composeFile);

        // Verify the deployment labels are serialized as direct key-value pairs
        // instead of nested under "additional_labels"
        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task GetHostAddressExpression()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddDockerComposeEnvironment("env");

        var project = builder
            .AddProject<Projects.ServiceA>("Project1", launchProfileName: null)
            .WithHttpEndpoint();

        var endpointReferenceEx = ((IComputeEnvironmentResource)env.Resource).GetHostAddressExpression(project.GetEndpoint("http"));
        Assert.NotNull(endpointReferenceEx);

        Assert.Equal("project1", endpointReferenceEx.Format);
        Assert.Empty(endpointReferenceEx.ValueProviders);
    }

    private sealed class MockImageBuilder : IResourceContainerImageManager
    {
        public bool BuildImageCalled { get; private set; }

        public Task BuildImageAsync(IResource resource, CancellationToken cancellationToken = default)
        {
            BuildImageCalled = true;
            return Task.CompletedTask;
        }

        public Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken = default)
        {
            BuildImageCalled = true;
            return Task.CompletedTask;
        }

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    [RequiresDocker]
    public async Task DockerComposeProjectNameIncludesAppHostShaInArguments()
    {
        using var tempDir = new TestTempDirectory();
        var testSink = new TestSink();

        // Set a known AppHost SHA in configuration
        const string testSha = "ABC123DEF456789ABCDEF123456789ABCDEF123456789ABCDEF123456789ABC";

        // Helper to create a builder with the same configuration
        IDistributedApplicationTestingBuilder CreateBuilder(string step)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: step);

            // Add TestLoggerProvider to capture logs during publish, set minimum level to Debug
            builder.Services.AddLogging(logging =>
            {
                logging.AddProvider(new TestLoggerProvider(testSink));
                logging.SetMinimumLevel(LogLevel.Debug);
            });

            builder.Configuration["AppHost:PathSha256"] = testSha;
            builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

            builder.AddDockerComposeEnvironment("my-environment");
            builder.AddContainer("service", "nginx");

            return builder;
        }

        try
        {
            // Deploy the application
            using (var deployApp = CreateBuilder(WellKnownPipelineSteps.Deploy).Build())
            {
                await deployApp.RunAsync();
            }

            // Verify that docker-compose.yaml was created
            var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
            Assert.True(File.Exists(composePath));

            // Check for docker compose up command with project name
            var expectedProjectName = "aspire-my-environment-abc123de";

            var logMessages = testSink.Writes.Select(w => w.Message).ToList();
            Assert.Contains(logMessages, msg =>
                msg != null &&
                msg.Contains("compose", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("--project-name", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains(expectedProjectName, StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("up", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Clean up using the built-in docker-compose-down pipeline step
            using var cleanupApp = CreateBuilder("docker-compose-down-my-environment").Build();
            await cleanupApp.RunAsync();
        }
    }

    [Fact]
    public async Task DashboardPrintSummaryStepIsCreatedAndWiredCorrectly()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        // Add a Docker Compose environment with dashboard enabled (default)
        builder.AddDockerComposeEnvironment("env")
               .WithDashboard();

        // Add a sample service to force compose generation
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        // In diagnostics mode, just verify the dashboard print step is created
        var logs = mockActivityReporter.LoggedMessages
                        .Where(s => s.StepTitle == "diagnostics")
                        .Select(s => s.Message)
                        .ToList();

        // Verify the dashboard print-summary step exists (named after the dashboard resource)
        Assert.Contains(logs, msg => msg.Contains("print-env-dashboard-summary"));

        // Verify it depends on docker-compose-up-env
        var stepDependencyLines = logs.Where(l => l.Contains("print-env-dashboard-summary")).ToList();
        Assert.Contains(stepDependencyLines, msg => msg.Contains("docker-compose-up-env"));
    }

    [Fact]
    [RequiresDocker]
    public async Task DeployWithDashboard_PrintsDashboardAndServiceEndpoints()
    {
        using var tempDir = new TestTempDirectory();
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        // Use a unique project name to avoid conflicts with other tests
        var projectId = Guid.NewGuid().ToString("N")[..8];

        // Use a random port in the dynamic/private port range (49152-65535) to avoid conflicts
        var hostPort = Random.Shared.Next(49152, 65535);
        output.WriteLine($"Using random host port: {hostPort}");

        // Helper to create a builder with the same configuration
        IDistributedApplicationTestingBuilder CreateBuilder(string step)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: step);
            builder.Configuration["AppHost:PathSha256"] = projectId;
            builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
            builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);
            builder.Services.AddLogging(logging => logging.AddXunit(output));

            // Add a Docker Compose environment with dashboard enabled
            builder.AddDockerComposeEnvironment("env")
                   .WithDashboard();

            // Add a container with an external endpoint
            // Use explicit port mapping so the URL can be displayed
            builder.AddContainer("nginx", "nginx:alpine")
                   .WithEndpoint(scheme: "http", port: hostPort, targetPort: 80, name: "http", isExternal: true);

            return builder;
        }

        try
        {
            // Deploy the application
            using (var deployApp = CreateBuilder(WellKnownPipelineSteps.Deploy).Build())
            {
                await deployApp.RunAsync();
            }

            // Verify the dashboard endpoint was logged
            var dashboardLogs = mockActivityReporter.LoggedMessages
                .Where(l => l.StepTitle.Contains("print-env-dashboard-summary", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Message)
                .ToList();

            output.WriteLine("Dashboard logs:");
            foreach (var log in dashboardLogs)
            {
                output.WriteLine($"  {log}");
            }

            Assert.Contains(dashboardLogs, msg => msg.Contains("env-dashboard"));

            // Verify the nginx endpoint was logged
            var nginxLogs = mockActivityReporter.LoggedMessages
                .Where(l => l.StepTitle.Contains("print-nginx-summary", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Message)
                .ToList();

            output.WriteLine("Nginx logs:");
            foreach (var log in nginxLogs)
            {
                output.WriteLine($"  {log}");
            }

            Assert.Contains(nginxLogs, msg => msg.Contains("nginx"));
            Assert.Contains(nginxLogs, msg => msg.Contains(hostPort.ToString())); // Verify the explicit port mapping is displayed
        }
        finally
        {
            // Clear the reporter to capture cleanup activity
            mockActivityReporter.Clear();

            // Clean up using the built-in docker-compose-down pipeline step
            output.WriteLine("Running cleanup: docker-compose-down-env step");
            using var cleanupApp = CreateBuilder("docker-compose-down-env").Build();
            await cleanupApp.RunAsync();

            // Verify the docker-compose-down step was executed
            var downSteps = mockActivityReporter.CreatedSteps
                .Where(s => s.Contains("docker-compose-down", StringComparison.OrdinalIgnoreCase))
                .ToList();

            output.WriteLine("Cleanup steps:");
            foreach (var step in mockActivityReporter.CreatedSteps)
            {
                output.WriteLine($"  {step}");
            }

            Assert.Contains(downSteps, s => s.Contains("docker-compose-down-env", StringComparison.OrdinalIgnoreCase));
            output.WriteLine("Cleanup complete");
        }
    }

    [Fact]
    public async Task FullRemoteImageName_WithNoRegistry_UsesLocalImageName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose");

        var project = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // With no registry, the local container registry is used which has an empty endpoint
        // This results in just the image name and tag
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        // The format should be just "imageName:tag" with no registry prefix
        Assert.Equal("servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_WithSingleRegistry_UsesRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(registry);

        var project = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // With a registry, the full image name includes the registry endpoint and repository
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("docker.io/myuser/servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_WithSingleRegistry_NoWithContainerRegistry_UsesRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose");
        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");

        var project = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // With a registry, the full image name includes the registry endpoint and repository
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("docker.io/myuser/servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_WithSingleRegistryNoRepository_UsesRegistryEndpointWithoutRepository()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var registry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io");
        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(registry);

        var project = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // With a registry without repository, the full image name includes just the registry endpoint
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("myregistry.azurecr.io/servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_WithMultipleRegistries_ResourceWithExplicitRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var registry1 = builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        var registry2 = builder.AddContainerRegistry("ghcr", "ghcr.io", "user2");

        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(registry1);

        // This project uses the explicit registry2 instead of the compose environment's default
        var project = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerRegistry(registry2);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // The project should use registry2 since it has an explicit WithContainerRegistry call
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("ghcr.io/user2/servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_ContainerResource_WithRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var registry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io", "myrepo");
        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(registry);

        // Container resource that will be built (e.g., from a Dockerfile)
        var container = builder.AddContainer("mycontainer", "nginx");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // The container should use the registry from the compose environment
        var containerImageReference = new ContainerImageReference(container.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("myregistry.azurecr.io/myrepo/mycontainer:latest", remoteImageName);
    }

    [Fact]
    public async Task FullRemoteImageName_WithAzureContainerRegistry_UsesRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // Add an Azure Container Registry - should be picked up automatically as IContainerRegistry
        var acr = builder.AddAzureContainerRegistry("myacr");
        acr.Resource.Outputs["loginServer"] = "myacr.azurecr.io";

        var composeEnv = builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(acr);

        var project = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // With Azure Container Registry, the full image name should use the ACR login server
        var containerImageReference = new ContainerImageReference(project.Resource);
        var remoteImageName = await ((IValueProvider)containerImageReference).GetValueAsync(default);

        Assert.Equal("myacr.azurecr.io/servicea:latest", remoteImageName);
    }

    [Fact]
    public async Task PushImageToRegistry_WithLocalRegistry_OnlyTagsImage()
    {
        using var tempDir = new TestTempDirectory();

        var fakeRuntime = new FakeContainerRuntime();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "push-servicea");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IContainerRuntime>(fakeRuntime);

        // No registry added - will use LocalContainerRegistry with empty endpoint
        builder.AddDockerComposeEnvironment("docker-compose");

        builder.AddProject<Projects.ServiceA>("servicea")
            .PublishAsDockerFile();

        using var app = builder.Build();
        await app.RunAsync();

        // Verify that TagImageAsync was called but PushImageAsync was not
        Assert.True(fakeRuntime.WasTagImageCalled, "TagImageAsync should have been called for local registry");
        Assert.False(fakeRuntime.WasPushImageCalled, "PushImageAsync should NOT have been called for local registry");

        // Verify the tag was applied correctly
        Assert.Single(fakeRuntime.TagImageCalls);
        var (localName, targetName) = fakeRuntime.TagImageCalls[0];
        Assert.StartsWith("servicea:", localName); // Local name includes a hash suffix
        Assert.StartsWith("servicea:", targetName); // Target name includes the deploy tag
    }

    [Fact]
    public async Task PushImageToRegistry_WithRemoteRegistry_PushesImage()
    {
        using var tempDir = new TestTempDirectory();

        var fakeRuntime = new FakeContainerRuntime();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "push-servicea");
        builder.Services.AddSingleton<IResourceContainerImageManager>(new MockImageBuilderWithRuntime(fakeRuntime));
        builder.Services.AddSingleton<IContainerRuntime>(fakeRuntime);

        // Add a remote registry with a non-empty endpoint
        var registry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io");
        builder.AddDockerComposeEnvironment("docker-compose")
            .WithContainerRegistry(registry);

        builder.AddProject<Projects.ServiceA>("servicea")
            .PublishAsDockerFile();

        using var app = builder.Build();
        await app.RunAsync();

        // Verify that PushImageAsync was called (which internally tags and pushes)
        Assert.True(fakeRuntime.WasPushImageCalled, "PushImageAsync should have been called for remote registry");
    }

    private sealed class MockImageBuilderWithRuntime(IContainerRuntime runtime) : IResourceContainerImageManager
    {
        public Task BuildImageAsync(IResource resource, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
            => runtime.PushImageAsync(resource, cancellationToken);
    }

    [Fact]
    public async Task DockerComposeUp_DependsOnPushSteps_WhenResourcesNeedToBePushed()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        // Add a Docker Compose environment
        builder.AddDockerComposeEnvironment("env");

        // Add a registry
        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");

        // Add a project resource that will need to be built and pushed
        builder.AddProject<Projects.ServiceA>("api")
            .PublishAsDockerFile();

        using var app = builder.Build();
        await app.RunAsync();

        // In diagnostics mode, verify the step dependencies
        var logs = mockActivityReporter.LoggedMessages
                        .Where(s => s.StepTitle == "diagnostics")
                        .Select(s => s.Message)
                        .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify docker-compose-up-env step exists
        Assert.Contains(logs, msg => msg.Contains("docker-compose-up-env"));

        // Verify push-api step exists
        Assert.Contains(logs, msg => msg.Contains("push-api"));

        // Verify docker-compose-up-env depends on push-api
        // The diagnostics output shows dependencies in the format: "step-name depends on: [dependencies]"
        var dockerComposeUpLines = logs.Where(l => l.Contains("docker-compose-up-env")).ToList();
        Assert.Contains(dockerComposeUpLines, msg => msg.Contains("push-api"));
    }

    [Fact]
    public async Task DockerComposeUp_DependsOnMultiplePushSteps_WhenMultipleResourcesNeedToBePushed()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        // Add a Docker Compose environment
        builder.AddDockerComposeEnvironment("env");

        // Add a registry
        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");

        // Add multiple project resources that will need to be built and pushed
        builder.AddProject<Projects.ServiceA>("api")
            .PublishAsDockerFile();

        builder.AddProject<Projects.ServiceA>("web")
            .PublishAsDockerFile();

        using var app = builder.Build();
        await app.RunAsync();

        // In diagnostics mode, verify the step dependencies
        var logs = mockActivityReporter.LoggedMessages
                        .Where(s => s.StepTitle == "diagnostics")
                        .Select(s => s.Message)
                        .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify docker-compose-up-env step exists
        Assert.Contains(logs, msg => msg.Contains("docker-compose-up-env"));

        // Verify both push steps exist
        Assert.Contains(logs, msg => msg.Contains("push-api"));
        Assert.Contains(logs, msg => msg.Contains("push-web"));

        // Verify docker-compose-up-env depends on both push steps
        var dockerComposeUpLines = logs.Where(l => l.Contains("docker-compose-up-env")).ToList();
        Assert.Contains(dockerComposeUpLines, msg => msg.Contains("push-api"));
        Assert.Contains(dockerComposeUpLines, msg => msg.Contains("push-web"));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
