// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Docker.Tests;

public class DockerComposePublisherTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PublishAsync_GeneratesValidDockerComposeFile()
    {
        using var tempDir = new TestTempDirectory();
        // Arrange

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        var param0 = builder.AddParameter("param0");
        var param1 = builder.AddParameter("param1", secret: true);
        var param2 = builder.AddParameter("param2", "default", publishValueAsDefault: true);
        var cs = builder.AddConnectionString("cs", ReferenceExpression.Create($"Url={param0}, Secret={param1}"));

        // Add a container to the application
        var redis = builder.AddContainer("cache", "redis")
                    .WithEntrypoint("/bin/sh")
                    .WithHttpEndpoint(name: "h2", port: 5000, targetPort: 5001)
                    .WithHttpEndpoint(env: "REDIS_PORT")
                    .WithArgs("-c", "hello $MSG")
                    .WithEnvironment("MSG", "world")
                    .WithContainerFiles("/usr/local/share", [
                        new ContainerFile
                        {
                            Name = "redis.conf",
                            Contents = "hello world",
                        },
                        new ContainerDirectory
                        {
                            Name = "folder",
                            Entries = [
                                new ContainerFile
                                {
                                    Name = "file.sh",
                                    SourcePath = "./hello.sh",
                                    Owner = 1000,
                                    Group = 1000,
                                    Mode = UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead,
                                },
                            ],
                        },
                    ])
                    .WithEnvironment(context =>
                    {
                        var resource = (IResourceWithEndpoints)context.Resource;

                        context.EnvironmentVariables["TP"] = resource.GetEndpoint("http").Property(EndpointProperty.TargetPort);
                        context.EnvironmentVariables["TPH2"] = resource.GetEndpoint("h2").Property(EndpointProperty.TargetPort);
                    });

        var migration = builder.AddContainer("something", "dummy/migration:latest")
                         .WithContainerName("cn");

        var api = builder.AddContainer("myapp", "mcr.microsoft.com/dotnet/aspnet:8.0")
                         .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                         .WithHttpEndpoint(env: "PORT")
                         .WithEnvironment("param0", param0)
                         .WithEnvironment("param1", param1)
                         .WithEnvironment("param2", param2)
                         .WithReference(cs)
                         .WithArgs("--cs", cs.Resource)
                         .WaitFor(redis)
                         .WaitForCompletion(migration)
                         .WaitFor(param0)
                         .WithOtlpExporter();

        builder.AddProject(
            "project1",
            "..\\TestingAppHost1\\TestingAppHost1.MyWebApp\\TestingAppHost1.MyWebApp.csproj",
            launchProfileName: null)
            .WithReference(api.GetEndpoint("http"));

        var app = builder.Build();

        // Act
        app.Run();

        // Assert
        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task DockerComposeWithProjectResources()
    {
        using var tempDir = new TestTempDirectory();
        // Arrange

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        // Add a project with multiple endpoint combinations
        var project = builder.AddProject<TestProjectWithLaunchSettings>("project1")
            .WithHttpEndpoint(name: "custom1") // port = null, targetPort = null
            .WithHttpEndpoint(port: 7001, name: "custom2") // port = 7001, targetPort = null
            .WithHttpEndpoint(targetPort: 7002, name: "custom3") // port = null, targetPort = 7002
            .WithHttpEndpoint(port: 7003, targetPort: 7004, name: "custom4"); // port = 7003, targetPort = 7004

        builder.AddContainer("api", "reg:api")
               .WithReference(project);

        var app = builder.Build();

        // Act
        app.Run();

        // Assert
        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task DockerComposeCorrectlyEmitsPortMappings()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path)
            .WithTestAndResourceLogging(outputHelper);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        builder.AddContainer("resource", "mcr.microsoft.com/dotnet/aspnet:8.0")
               .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
               .WithHttpEndpoint(env: "HTTP_PORT");

        var app = builder.Build();

        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        await Verify(File.ReadAllText(composePath), "yaml");
    }

    [Fact]
    public void DockerComposeDoesNotHandleImageBuildingDuringPublish()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "publish-docker-compose")
            .WithTestAndResourceLogging(outputHelper);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose");

        builder.AddContainer("resource", "mcr.microsoft.com/dotnet/aspnet:8.0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithHttpEndpoint(env: "HTTP_PORT");

        var app = builder.Build();

        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;

        Assert.NotNull(mockImageBuilder);

        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));
        Assert.False(mockImageBuilder.BuildImageCalled);
    }

    [Fact]
    public async Task DockerComposeAppliesServiceCustomizations()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var containerNameParam = builder.AddParameter("param-1", "default-name", publishValueAsDefault: true);

        builder.AddDockerComposeEnvironment("docker-compose")
               .WithProperties(e => e.DefaultNetworkName = "default-network")
               .ConfigureComposeFile(file =>
               {
                   file.AddNetwork(new Network { Name = "custom-network", Driver = "host" });

                   file.Name = "my application";
               });

        // Add a container to the application
        var container = builder.AddContainer("service", "nginx")
            .WithEnvironment("ORIGINAL_ENV", "value")
            .PublishAsDockerComposeService((serviceResource, composeService) =>
            {
                // Add a custom label
                composeService.Labels["custom-label"] = "test-value";

                // Add a custom environment variable
                composeService.AddEnvironmentalVariable("CUSTOM_ENV", "custom-value");

                // Set a restart policy
                composeService.Restart = "always";

                composeService.ContainerName = containerNameParam.AsEnvironmentPlaceholder(serviceResource);

                // Add a custom network
                composeService.Networks.Add("custom-network");
            });

        var app = builder.Build();

        app.Run();

        // Assert
        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task DockerComposeDoesNotOverwriteEnvFileOnPublish()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        void PublishApp()
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
            builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

            builder.AddDockerComposeEnvironment("docker-compose");
            var param = builder.AddParameter("param1");
            builder.AddContainer("app", "busybox").WithEnvironment("param1", param);
            var app = builder.Build();
            app.Run();
        }

        PublishApp();
        Assert.True(File.Exists(envFilePath));
        var firstContent = File.ReadAllText(envFilePath).Replace("PARAM1=", "PARAM1=changed");
        File.WriteAllText(envFilePath, firstContent);

        PublishApp();
        Assert.True(File.Exists(envFilePath));
        var secondContent = File.ReadAllText(envFilePath);

        await Verify(firstContent, "env")
            .AppendContentAsFile(secondContent, "env");
    }

    [Fact]
    public async Task DockerComposeAppendsNewKeysToEnvFileOnPublish()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        void PublishApp(params string[] paramNames)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
            builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

            builder.AddDockerComposeEnvironment("docker-compose");

            var parmeters = paramNames.Select(name => builder.AddParameter(name).Resource).ToArray();

            builder.AddContainer("app", "busybox")
                    .WithEnvironment(context =>
                    {
                        foreach (var param in parmeters)
                        {
                            context.EnvironmentVariables[param.Name] = param;
                        }
                    });

            var app = builder.Build();
            app.Run();
        }

        PublishApp(["param1"]);
        Assert.True(File.Exists(envFilePath));
        var firstContent = File.ReadAllText(envFilePath).Replace("PARAM1=", "PARAM1=changed");
        File.WriteAllText(envFilePath, firstContent);

        PublishApp(["param1", "param2"]);
        Assert.True(File.Exists(envFilePath));
        var secondContent = File.ReadAllText(envFilePath);

        await Verify(firstContent, "env")
            .AppendContentAsFile(secondContent, "env");
    }

    [Fact]
    public async Task DockerComposeMapsPortsProperly()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        var container = builder.AddExecutable("service", "foo", ".")
            .PublishAsDockerFile()
            .WithHttpEndpoint(env: "PORT");

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeFile = File.ReadAllText(composePath);

        await Verify(composeFile);
    }

    [Fact]
    public async Task PublishAsync_WithDashboardEnabled_IncludesDashboardService()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(); // Dashboard enabled by default

        // Add a container with OTLP exporter
        builder.AddContainer("api", "my-api")
            .WithOtlpExporter();

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeContent = File.ReadAllText(composePath);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_WithDashboardDisabled_DoesNotIncludeDashboardService()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        // Add a container with OTLP exporter
        builder.AddContainer("api", "my-api")
            .WithOtlpExporter();

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeContent = File.ReadAllText(composePath);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_WithDashboard_UsesCustomConfiguration()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(dashboard =>
            {
                dashboard.WithImage("custom-dashboard:latest")
                    .WithContainerName("custom-dashboard")
                    .WithEnvironment("CUSTOM_VAR", "custom-value")
                    .WithHostPort(8081);
            });

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeContent = File.ReadAllText(composePath);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_MultipleResourcesWithOtlp_ConfiguresAllForDashboard()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard();

        // Add multiple containers with OTLP exporter
        builder.AddContainer("api", "my-api")
            .WithOtlpExporter();

        builder.AddContainer("worker", "my-worker")
            .WithOtlpExporter();

        // Add a container without OTLP - should not be configured
        builder.AddContainer("database", "postgres");

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeContent = File.ReadAllText(composePath);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_WithDockerfileFactory_WritesDockerfileToOutputFolder()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.AddDockerComposeEnvironment("docker-compose");

        var dockerfileContent = "FROM alpine:latest\nRUN echo 'Generated for docker compose'";
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

    [Fact]
    public void PublishAsync_InRunMode_DoesNotCreateDashboard()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(); // Should be ignored in run mode

        builder.AddContainer("api", "my-api")
            .WithOtlpExporter();

        var app = builder.Build();

        // In run mode, no compose file should be generated
        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.False(File.Exists(composePath));
    }

    [Fact]
    public async Task PrepareStep_GeneratesCorrectEnvFileWithDefaultEnvironmentName()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "prepare-docker-compose");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Configuration["ConnectionStrings:cstest"] = "Server=localhost;Database=test";

        var environment = builder.AddDockerComposeEnvironment("docker-compose");

        var param1 = builder.AddParameter("param1", "defaultValue1");
        var param2 = builder.AddParameter("param2", "defaultSecretValue", secret: true);
        var cs = builder.AddConnectionString("cstest");

        builder.AddContainer("testapp", "testimage")
            .WithEnvironment("PARAM1", param1)
            .WithEnvironment("PARAM2", param2)
            .WithReference(cs);

        var app = builder.Build();
        app.Run();

        var envFileContent = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, ".env.Production"));
        await Verify(envFileContent, "env")
            .UseParameters("default-environment");
    }

    [Fact]
    public async Task PrepareStep_GeneratesCorrectEnvFileWithCustomEnvironmentName()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "prepare-docker-compose");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // Add a custom IHostEnvironment with a specific environment name
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment("Staging"));

        var environment = builder.AddDockerComposeEnvironment("docker-compose");

        var param1 = builder.AddParameter("param1", "stagingValue");
        var param2 = builder.AddParameter("param2", "defaultStagingSecret", secret: true);

        builder.AddContainer("testapp", "testimage")
            .WithEnvironment("PARAM1", param1)
            .WithEnvironment("PARAM2", param2);

        var app = builder.Build();
        app.Run();

        // Verify that the env file is created with the custom environment name
        var envFilePath = Path.Combine(tempDir.Path, ".env.Staging");
        Assert.True(File.Exists(envFilePath), $"Expected env file at {envFilePath}");

        var envFileContent = await File.ReadAllTextAsync(envFilePath);
        await Verify(envFileContent, "env")
            .UseParameters("custom-environment");
    }

    [Fact]
    public async Task PrepareStep_GeneratesEnvFileWithVariousParameterTypes()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "prepare-docker-compose");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Configuration["ConnectionStrings:dbConnection"] = "Server=localhost;Database=mydb";

        var environment = builder.AddDockerComposeEnvironment("docker-compose");

        // Various parameter types
        var stringParam = builder.AddParameter("stringParam", "defaultString");
        var secretParam = builder.AddParameter("secretParam", "defaultSecretParameter", secret: true);
        var paramWithDefault = builder.AddParameter("paramWithDefault", "defaultValue", publishValueAsDefault: true);
        var cs = builder.AddConnectionString("dbConnection");

        builder.AddContainer("webapp", "webapp:latest")
            .WithEnvironment("STRING_PARAM", stringParam)
            .WithEnvironment("SECRET_PARAM", secretParam)
            .WithEnvironment("PARAM_WITH_DEFAULT", paramWithDefault)
            .WithReference(cs);

        var app = builder.Build();
        app.Run();

        var envFileContent = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, ".env.Production"));
        await Verify(envFileContent, "env")
            .UseParameters("various-parameters");
    }

    [Fact]
    public void PrepareStep_OverwritesExistingEnvFileAndLogsWarning()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "prepare-docker-compose");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.WithTestAndResourceLogging(outputHelper);

        var environment = builder.AddDockerComposeEnvironment("docker-compose");

        var param1 = builder.AddParameter("param1", "defaultValue1");

        builder.AddContainer("testapp", "testimage")
            .WithEnvironment("PARAM1", param1);

        // Pre-create the env file to simulate it already existing
        var envFilePath = Path.Combine(tempDir.Path, ".env.Production");
        File.WriteAllText(envFilePath, "# Old content\nOLD_KEY=old_value\n");

        var app = builder.Build();
        app.Run();

        // Verify the file was overwritten with new content
        var envFileContent = File.ReadAllText(envFilePath);
        Assert.Contains("PARAM1", envFileContent);
        Assert.DoesNotContain("OLD_KEY", envFileContent);

        // The log message should be captured by the test output helper
        // We can verify it was called by checking the test output
        // The xunit logger will output to outputHelper
    }

    [Fact]
    public void PrepareStep_OverwritesExistingEnvFileWithCustomEnvironmentName()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "prepare-docker-compose");
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment("Staging"));
        builder.WithTestAndResourceLogging(outputHelper);

        var environment = builder.AddDockerComposeEnvironment("docker-compose");

        var param1 = builder.AddParameter("param1", "stagingValue");

        builder.AddContainer("testapp", "testimage")
            .WithEnvironment("PARAM1", param1);

        // Pre-create the env file with custom environment name
        var envFilePath = Path.Combine(tempDir.Path, ".env.Staging");
        File.WriteAllText(envFilePath, "# Old staging content\nOLD_STAGING_KEY=old_staging_value\n");

        var app = builder.Build();
        app.Run();

        // Verify the file was overwritten with new content
        var envFileContent = File.ReadAllText(envFilePath);
        Assert.Contains("PARAM1", envFileContent);
        Assert.DoesNotContain("OLD_STAGING_KEY", envFileContent);
    }

    [Fact]
    public async Task PublishAsync_BindMounts_ReplacedWithEnvironmentPlaceholders()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        // Add a container with bind mounts
        builder.AddContainer("my-container", "my-image")
            .WithBindMount("/host/path/data", "/container/data")
            .WithBindMount("/another/host/path", "/container/config", isReadOnly: true);

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task PublishAsync_DockerSocket_NotReplacedWithPlaceholder()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        // Add a container with docker socket mount
        builder.AddContainer("docker-proxy", "my-image")
            .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock");

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        Assert.True(File.Exists(composePath));

        var composeContent = File.ReadAllText(composePath);

        // Docker socket should be preserved as-is, not replaced with placeholder
        Assert.Contains("/var/run/docker.sock", composeContent);

        await Verify(composeContent, "yaml");
    }

    [Fact]
    public async Task PublishAsync_MixedBindMountsAndVolumes()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        // Add a container with both bind mounts and volumes
        builder.AddContainer("my-service", "my-image")
            .WithVolume("my-volume", "/container/volume-data")
            .WithBindMount("/host/path/data", "/container/bind-data")
            .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock");

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task PublishAsync_ConfigureEnvFile_AllowsMutatingCapturedEnvVars()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false)
            .ConfigureEnvFile(envVars =>
            {
                // Modify the default value of a bind mount env var
                foreach (var envVar in envVars.Values)
                {
                    if (envVar.Source is ContainerMountAnnotation mount)
                    {
                        // Change the default path to a relative path
                        envVar.DefaultValue = "./" + Path.GetFileName(mount.Source);
                    }
                }
            });

        // Add a container with bind mounts
        builder.AddContainer("my-container", "my-image")
            .WithBindMount("/host/path/data", "/container/data");

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task PublishAsync_ConfigureEnvFile_CanRemoveGeneratedPlaceholder()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // Create a parameter for the cert directory
        var certDir = builder.AddParameter("certDir", secret: false);

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false)
            .ConfigureEnvFile(envVars =>
            {
                // Find and remove the auto-generated bind mount placeholder for yarp
                var keysToRemove = envVars.Where(kv => 
                    kv.Value.Resource?.Name == "yarp" && 
                    kv.Value.Source is ContainerMountAnnotation).Select(kv => kv.Key).ToList();

                foreach (var key in keysToRemove)
                {
                    envVars.Remove(key);
                }
            });

        // Add a container (yarp) with a bind mount
        var yarp = builder.AddContainer("yarp", "my-image")
            .WithBindMount("./certs", "/app/certs", isReadOnly: true);

        // Override the volume source with a parameter placeholder
        yarp.PublishAsDockerComposeService((svc, infra) =>
        {
            infra.Volumes[0].Source = certDir.AsEnvironmentPlaceholder(svc);
        });

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
    }

    [Fact]
    public async Task PublishAsync_WindowsAbsoluteBindMountPath_ReplacedWithEnvironmentPlaceholders()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test only runs on Windows");
            return;
        }

        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("docker-compose")
            .WithDashboard(false);

        // Add a container with Windows-specific absolute path bind mounts
        builder.AddContainer("my-container", "my-image")
            .WithBindMount(@"C:\Users\TestUser\Data", "/container/data")
            .WithBindMount(@"D:\AppConfig", "/container/config", isReadOnly: true);

        var app = builder.Build();
        app.Run();

        var composePath = Path.Combine(tempDir.Path, "docker-compose.yaml");
        var envPath = Path.Combine(tempDir.Path, ".env");
        Assert.True(File.Exists(composePath));
        Assert.True(File.Exists(envPath));

        await Verify(File.ReadAllText(composePath), "yaml")
            .AppendContentAsFile(File.ReadAllText(envPath), "env");
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

    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "TestApplication";
        public string ContentRootPath { get; set; } = "/test";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
