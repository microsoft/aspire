// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREFILESYSTEM001

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Publishing;

public class ResourceContainerImageBuilderTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResourceWithCustomBaseImage()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithDockerfileBaseImage(runtimeImage: "mcr.microsoft.com/dotnet/sdk:8.0-alpine");
#pragma warning restore ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains("/p:ContainerBaseImage=\"mcr.microsoft.com/dotnet/sdk:8.0-alpine\""));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresDocker]
    [ActiveIssue("https://github.com/dotnet/dnceng/issues/6232", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task CanBuildImageFromDockerfileResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var servicea = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource container"));
        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));
    }

    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResourceWithOptions()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = "/tmp/test-output";
                ctx.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));

        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));
    }

    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResource_WithDockerImageFormat()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx => ctx.ImageFormat = ContainerImageFormat.Docker);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResource_WithLinuxArm64Platform()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx => ctx.TargetPlatform = ContainerTargetPlatform.LinuxArm64);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresDocker]
    [ActiveIssue("https://github.com/dotnet/dnceng/issues/6232", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task CanBuildImageFromDockerfileResource_WithCustomOutputPath()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempOutputPath = Path.GetTempPath();
        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
            .WithContainerBuildOptions(ctx =>
            {
                ctx.OutputPath = tempOutputPath;
                ctx.ImageFormat = ContainerImageFormat.Oci;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource container"));
        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));
    }

    [Fact]
    [RequiresDocker]
    [ActiveIssue("https://github.com/dotnet/dnceng/issues/6232", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task CanBuildImageFromDockerfileResource_WithAllOptionsSet()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        using var tempDir = new TestTempDirectory();
        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
            .WithContainerBuildOptions(ctx =>
            {
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = Path.Combine(tempDir.Path, "NewFolder"); // tests that the folder is created if it doesn't exist
                ctx.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource container"));

        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));
    }

    [Theory]
    [InlineData(ContainerImageFormat.Docker)]
    [InlineData(ContainerImageFormat.Oci)]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResource_WithDifferentImageFormats(ContainerImageFormat imageFormat)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx => ctx.ImageFormat = imageFormat);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Theory]
    [InlineData(ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerTargetPlatform.LinuxArm64)]
    [RequiresDocker]
    public async Task CanBuildImageFromProjectResource_WithDifferentTargetPlatforms(ContainerTargetPlatform targetPlatform)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx => ctx.TargetPlatform = targetPlatform);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresDocker]
    public async Task BuildImageAsync_WithNullOptions_UsesDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Test without explicit options - should use defaults from annotation
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource servicea"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    public void ContainerImageBuildOptions_CanSetAllProperties()
    {
        var options = new ContainerImageBuildOptions
        {
            ImageFormat = ContainerImageFormat.Oci,
            OutputPath = "/custom/path",
            TargetPlatform = ContainerTargetPlatform.LinuxArm64
        };

        Assert.Equal(ContainerImageFormat.Oci, options.ImageFormat);
        Assert.Equal("/custom/path", options.OutputPath);
        Assert.Equal(ContainerTargetPlatform.LinuxArm64, options.TargetPlatform);
    }

    [Fact]
    [RequiresDocker]
    [ActiveIssue("https://github.com/dotnet/dnceng/issues/6232", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task CanBuildImageFromDockerfileResource_WithTrailingSlashContextPath()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        // Add trailing slashes to simulate the issue scenario
        var contextPathWithTrailingSlash = tempContextPath + Path.DirectorySeparatorChar;
        var servicea = builder.AddDockerfile("container", contextPathWithTrailingSlash, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // This should not fail even with trailing slash in context path
        await imageBuilder.BuildImageAsync(servicea.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource container"));
        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));
    }

    [Fact]
    public async Task PushImageAsync_CallsContainerRuntimePushImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var testResource = builder.AddContainer("test-image", "test-image:latest");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Act
        await imageBuilder.PushImageAsync(testResource.Resource, cts.Token);

        // Assert
        Assert.True(fakeContainerRuntime.WasPushImageCalled);
        Assert.Collection(fakeContainerRuntime.PushImageCalls,
            resource => Assert.Equal(testResource.Resource, resource));
    }

    [Fact]
    public async Task PushImageAsync_ThrowsWhenContainerRuntimeFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: true);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var testResource = builder.AddContainer("test-image", "test-image:latest");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            imageBuilder.PushImageAsync(testResource.Resource, cts.Token));

        Assert.Equal("Fake container runtime is configured to fail", exception.Message);
        Assert.True(fakeContainerRuntime.WasPushImageCalled);
    }

    [Fact]
    public async Task BuildImagesAsync_WithOnlyProjectResourcesAndOci_DoesNotNeedContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime that would fail if called
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: true);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = "/tmp/test-path";
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // This should not fail despite the fake container runtime being configured to fail
        // because we only have project resources (no DockerfileBuildAnnotation)
        await imageBuilder.BuildImagesAsync([servicea.Resource], cts.Token);

        // Validate that the container runtime health check was not called
        Assert.False(fakeContainerRuntime.WasHealthCheckCalled);
    }

    [Fact]
    public async Task BuildImagesAsync_WithDockerfileResources_ChecksContainerRuntimeHealth()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime that tracks health check calls
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var dockerfileResource = builder.AddDockerfile("test-dockerfile", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImagesAsync([dockerfileResource.Resource], cts.Token);

        // Validate that the container runtime health check was called for resources with DockerfileBuildAnnotation
        Assert.True(fakeContainerRuntime.WasHealthCheckCalled);
    }

    [Fact]
    public async Task BuildImageAsync_NormalizesContextPathWithTrailingSlashes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime that captures the actual context path used
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        // Add trailing slashes to context path to test normalization
        var contextPathWithTrailingSlash = tempContextPath + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;
        var dockerfileResource = builder.AddDockerfile("test-dockerfile", contextPathWithTrailingSlash, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImagesAsync([dockerfileResource.Resource], cts.Token);

        // Verify that the fake runtime was called to build the image
        Assert.True(fakeContainerRuntime.WasBuildImageCalled);
        Assert.Single(fakeContainerRuntime.BuildImageCalls);

        var buildCall = fakeContainerRuntime.BuildImageCalls[0];

        // The context path should be normalized (no trailing slashes)
        Assert.False(buildCall.contextPath.EndsWith(Path.DirectorySeparatorChar.ToString()));
        Assert.False(buildCall.contextPath.EndsWith(Path.AltDirectorySeparatorChar.ToString()));

        // It should still point to the same directory
        Assert.Equal(Path.GetFullPath(tempContextPath), Path.GetFullPath(buildCall.contextPath));
    }

    [Fact]
    public async Task BuildImageAsync_ThrowsInvalidOperationException_WhenDockerRuntimeNotAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", new FakeContainerRuntime(shouldFail: true));

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            imageBuilder.BuildImagesAsync([container.Resource], cts.Token));

        Assert.Contains("Container runtime", exception.Message);
        Assert.Contains("is not running or is unhealthy", exception.Message);

        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        Assert.Contains(logs, log => log.Message.Contains("is not running or is unhealthy. Cannot build container images."));
    }

    [Fact]
    [RequiresDocker]
    public async Task CanBuildImageFromDockerfileWithBuildArgsSecretsAndStage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime to capture build arguments and secrets
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        // Add parameters for build args and secrets
        builder.Configuration["Parameters:goversion"] = "1.22";
        builder.Configuration["Parameters:secret"] = "mysecret";

        var goVersionParam = builder.AddParameter("goversion");
        var secretParam = builder.AddParameter("secret", secret: true);

        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath, stage: "runner")
                              .WithBuildArg("GO_VERSION", goVersionParam)
                              .WithBuildArg("STATIC_ARG", "static-value")
                              .WithBuildSecret("SECRET_ASENV", secretParam);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

        // Validate that BuildImageAsync succeeded by checking the log output
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Check for success logs
        Assert.Contains(logs, log => log.Message.Contains("Building container image for resource container"));
        // Ensure no error logs were produced during the build process
        Assert.DoesNotContain(logs, log => log.Level >= LogLevel.Error &&
            log.Message.Contains("Failed to build container image"));

        // Verify that the correct build arguments were passed
        Assert.NotNull(fakeContainerRuntime.CapturedBuildArguments);
        Assert.Equal(2, fakeContainerRuntime.CapturedBuildArguments.Count);
        Assert.Equal("1.22", fakeContainerRuntime.CapturedBuildArguments["GO_VERSION"]);
        Assert.Equal("static-value", fakeContainerRuntime.CapturedBuildArguments["STATIC_ARG"]);

        // Verify that the correct build secrets were passed
        Assert.NotNull(fakeContainerRuntime.CapturedBuildSecrets);
        Assert.Single(fakeContainerRuntime.CapturedBuildSecrets);
        Assert.Equal("mysecret", fakeContainerRuntime.CapturedBuildSecrets["SECRET_ASENV"]);

        // Verify that the correct stage was passed
        Assert.Equal("runner", fakeContainerRuntime.CapturedStage);
    }

    [Fact]
    public async Task CanResolveBuildArgumentsWithDifferentValueTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime to capture build arguments
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        // Add parameters for different value types
        builder.Configuration["Parameters:stringparam"] = "test-value";
        builder.Configuration["Parameters:valueprovider"] = "provider-value";
        var stringParam = builder.AddParameter("stringparam");
        var valueProviderParam = builder.AddParameter("valueprovider");

        // Create a temporary file to test FileInfo handling
        var tempFile = Path.GetTempFileName();
        var fileInfo = new FileInfo(tempFile);

        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
                              .WithBuildArg("STRING_ARG", stringParam)
                              .WithBuildArg("BOOL_TRUE_ARG", true)
                              .WithBuildArg("BOOL_FALSE_ARG", false)
                              .WithBuildArg("NULL_ARG", (string?)null)
                              .WithBuildArg("DIRECT_STRING_ARG", "direct-string")
                              .WithBuildArg("EMPTY_STRING_ARG", "")
                              .WithBuildArg("FILEINFO_ARG", fileInfo)
                              .WithBuildArg("VALUEPROVIDER_ARG", valueProviderParam)
                              .WithBuildArg("INT_ARG", 42)
                              .WithBuildArg("DECIMAL_ARG", 3.14);

        using var app = builder.Build();

        try
        {
            using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
            var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
            await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

            // Verify that different value types are resolved correctly
            Assert.NotNull(fakeContainerRuntime.CapturedBuildArguments);
            Assert.Equal(10, fakeContainerRuntime.CapturedBuildArguments.Count);

            // Parameter should resolve to its configured value (IValueProvider)
            Assert.Equal("test-value", fakeContainerRuntime.CapturedBuildArguments["STRING_ARG"]);

            // Boolean values should be converted to strings
            Assert.Equal("true", fakeContainerRuntime.CapturedBuildArguments["BOOL_TRUE_ARG"]);
            Assert.Equal("false", fakeContainerRuntime.CapturedBuildArguments["BOOL_FALSE_ARG"]);

            // Null should be converted to null (not empty string)
            Assert.Null(fakeContainerRuntime.CapturedBuildArguments["NULL_ARG"]);

            // Direct string should be passed through
            Assert.Equal("direct-string", fakeContainerRuntime.CapturedBuildArguments["DIRECT_STRING_ARG"]);

            // Empty string should be passed through
            Assert.Equal("", fakeContainerRuntime.CapturedBuildArguments["EMPTY_STRING_ARG"]);

            // FileInfo should resolve to its FullName
            Assert.Equal(tempFile, fakeContainerRuntime.CapturedBuildArguments["FILEINFO_ARG"]);

            // IValueProvider (parameter) should resolve to its configured value
            Assert.Equal("provider-value", fakeContainerRuntime.CapturedBuildArguments["VALUEPROVIDER_ARG"]);

            // Integer should be converted to string via ToString()
            Assert.Equal("42", fakeContainerRuntime.CapturedBuildArguments["INT_ARG"]);

            // Decimal should be converted to string via ToString()
            Assert.Equal("3.14", fakeContainerRuntime.CapturedBuildArguments["DECIMAL_ARG"]);
        }
        finally
        {
            // Clean up the temporary file
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ResolveValue_FormatsDecimalWithInvariantCulture()
    {
        // Test decimal value
        var result = await ResourceContainerImageManager.ResolveValue(3.14, CancellationToken.None);
        Assert.Equal("3.14", result);

        // Test double value
        result = await ResourceContainerImageManager.ResolveValue(3.14d, CancellationToken.None);
        Assert.Equal("3.14", result);

        // Test float value
        result = await ResourceContainerImageManager.ResolveValue(3.14f, CancellationToken.None);
        Assert.Equal("3.14", result);

        // Test integer (should also work)
        result = await ResourceContainerImageManager.ResolveValue(42, CancellationToken.None);
        Assert.Equal("42", result);
    }

    [Fact]
    public async Task CanResolveBuildSecretsWithDifferentValueTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime to capture build secrets
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        // Add parameters for different value types
        builder.Configuration["Parameters:stringsecret"] = "secret-value";
        builder.Configuration["Parameters:nullsecret"] = null;
        var stringSecret = builder.AddParameter("stringsecret", secret: true);
        var nullSecret = builder.AddParameter("nullsecret", secret: true);

        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
                              .WithBuildSecret("STRING_SECRET", stringSecret)
                              .WithBuildSecret("NULL_SECRET", nullSecret);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

        // Verify that different value types are resolved correctly
        Assert.NotNull(fakeContainerRuntime.CapturedBuildSecrets);
        Assert.Equal(2, fakeContainerRuntime.CapturedBuildSecrets.Count);

        // Parameter should resolve to its configured value
        Assert.Equal("secret-value", fakeContainerRuntime.CapturedBuildSecrets["STRING_SECRET"]);

        // Null parameter should resolve to null
        Assert.Null(fakeContainerRuntime.CapturedBuildSecrets["NULL_SECRET"]);
    }

    [Fact]
    public async Task MultipleAnnotations_AppliedInOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(ctx =>
            {
                ctx.LocalImageName = "first-name";
                ctx.LocalImageTag = "first-tag";
                ctx.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            }))
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(ctx =>
            {
                ctx.ImageFormat = ContainerImageFormat.Oci;
            }));

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await servicea.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.Equal("first-name", context.LocalImageName);
        Assert.Equal("first-tag", context.LocalImageTag);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, context.TargetPlatform);
        Assert.Equal(ContainerImageFormat.Oci, context.ImageFormat);
    }

    [Fact]
    public async Task LaterAnnotation_OverridesEarlierAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(ctx =>
            {
                ctx.LocalImageName = "first-name";
                ctx.LocalImageTag = "first-tag";
                ctx.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
                ctx.ImageFormat = ContainerImageFormat.Docker;
            }))
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(ctx =>
            {
                ctx.LocalImageName = "second-name";
                ctx.ImageFormat = ContainerImageFormat.Oci;
            }));

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await servicea.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.Equal("second-name", context.LocalImageName);
        Assert.Equal("first-tag", context.LocalImageTag);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, context.TargetPlatform);
        Assert.Equal(ContainerImageFormat.Oci, context.ImageFormat);
    }

    [Fact]
    public async Task ProjectResource_HasDefaultAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await servicea.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.Equal("servicea", context.LocalImageName);
        Assert.Equal("latest", context.LocalImageTag);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, context.TargetPlatform);
    }

    [Fact]
    public async Task DockerfileResource_HasDefaultAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var container = builder.AddDockerfile("mycontainer", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await container.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        var dockerfileBuildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        var expectedImageTag = dockerfileBuildAnnotation.ImageTag;

        Assert.Equal("mycontainer", context.LocalImageName);
        Assert.Equal(expectedImageTag, context.LocalImageTag);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, context.TargetPlatform);
    }

    [Fact]
    public async Task DockerfileResource_WithCustomImageName_UsesCustomValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        var dockerfileBuildAnnotation = new DockerfileBuildAnnotation(tempContextPath, tempDockerfilePath, null)
        {
            ImageName = "custom-image",
            ImageTag = "v1.0.0"
        };

        var container = builder.AddResource(new ContainerResource("mycontainer"))
            .WithAnnotation(dockerfileBuildAnnotation);

        var defaultContainerBuildOptions = new ContainerBuildOptionsCallbackAnnotation(context =>
        {
            if (context.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileAnnotation))
            {
                context.LocalImageName = dockerfileAnnotation.ImageName ?? context.Resource.Name;
                context.LocalImageTag = dockerfileAnnotation.ImageTag ?? "latest";
            }
        });

        container.WithAnnotation(defaultContainerBuildOptions);

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await container.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.Equal("custom-image", context.LocalImageName);
        Assert.Equal("v1.0.0", context.LocalImageTag);
    }

    [Fact]
    public async Task ContainerBuildOptionsCallbackAnnotation_AsyncCallback_IsSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(async ctx =>
            {
                await Task.Delay(1);
                ctx.LocalImageName = "async-name";
                ctx.LocalImageTag = "async-tag";
            }));

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var context = await servicea.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.Equal("async-name", context.LocalImageName);
        Assert.Equal("async-tag", context.LocalImageTag);
    }

    [Fact]
    public async Task ContainerBuildOptionsContext_HasCorrectResourceAndServices()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        IResource? capturedResource = null;
        IServiceProvider? capturedServices = null;

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                capturedResource = ctx.Resource;
                capturedServices = ctx.Services;
            });

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        await servicea.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(capturedResource);
        Assert.Equal(servicea.Resource, capturedResource);
        Assert.NotNull(capturedServices);
        Assert.Equal(app.Services, capturedServices);
    }

    [Fact]
    public async Task BuildImagesAsync_WithArchiveDestinationOnlyResources_DoesNotCheckContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Use FakeContainerRuntime that simulates Docker not running
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false, isRunning: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        using var tempDir = new TestTempDirectory();

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.OutputPath = Path.Combine(tempDir.Path, "archives");
                ctx.ImageFormat = ContainerImageFormat.Oci;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // The check for whether Docker is needed should not call CheckIfRunningAsync
        // when all resources are Archive destination. However, the actual build will fail
        // because we can't build without network access to get base images.
        // We're just verifying that CheckIfRunningAsync is not called in the upfront check.
        try
        {
            await imageManager.BuildImagesAsync([servicea.Resource], cts.Token);
        }
        catch (DistributedApplicationException)
        {
            // Expected to fail during actual build due to missing network/base images
            // But we should verify CheckIfRunningAsync was not called
        }
        catch (TaskCanceledException)
        {
            // Expected if build takes too long
        }

        // Verify CheckIfRunningAsync was not called in the upfront runtime check
        Assert.Equal(0, fakeContainerRuntime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task BuildImagesAsync_WithRegistryDestination_ChecksContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Use FakeContainerRuntime that simulates Docker not running
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false, isRunning: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Registry;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Should throw because container runtime is not running
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => imageManager.BuildImagesAsync([servicea.Resource], cts.Token));

        Assert.Contains("is not running or is unhealthy", exception.Message);

        // Verify CheckIfRunningAsync was called in the upfront check
        Assert.Equal(1, fakeContainerRuntime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task BuildImageAsync_DockerfileResource_RequiresContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Use FakeContainerRuntime that simulates Docker not running
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false, isRunning: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        var (tempContextPath, tempDockerfilePath) = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Should throw because Dockerfile builds always require Docker
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => imageManager.BuildImageAsync(container.Resource, cts.Token));

        Assert.Contains("is not running or is unhealthy", exception.Message);

        // Verify CheckIfRunningAsync was called in BuildImageAsync
        Assert.Equal(1, fakeContainerRuntime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task BuildImagesAsync_MixedDestinations_ChecksRuntimeWhenAnyResourceNeedsIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Use FakeContainerRuntime that simulates Docker not running
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false, isRunning: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        using var tempDir = new TestTempDirectory();

        // Add two projects: one with Archive destination, one without
        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.OutputPath = Path.Combine(tempDir.Path, "archives");
                ctx.ImageFormat = ContainerImageFormat.Oci;
            });

        var serviceb = builder.AddProject<Projects.ServiceB>("serviceb");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Should throw because serviceb requires Docker and it's not running
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => imageManager.BuildImagesAsync([servicea.Resource, serviceb.Resource], cts.Token));

        Assert.Contains("is not running or is unhealthy", exception.Message);

        // Verify CheckIfRunningAsync was called once for the entire batch
        Assert.Equal(1, fakeContainerRuntime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task BuildImagesAsync_NoDestinationSet_ChecksContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Use FakeContainerRuntime that simulates Docker not running
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false, isRunning: false);
        builder.Services.AddKeyedSingleton<IContainerRuntime>("docker", fakeContainerRuntime);

        // Project without any destination set should default to requiring Docker
        var servicea = builder.AddProject<Projects.ServiceA>("servicea");

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        // Should throw because container runtime is not running and no Archive destination is set
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => imageManager.BuildImagesAsync([servicea.Resource], cts.Token));

        Assert.Contains("is not running or is unhealthy", exception.Message);

        // Verify CheckIfRunningAsync was called
        Assert.Equal(1, fakeContainerRuntime.CheckIfRunningCallCount);
    }
}
