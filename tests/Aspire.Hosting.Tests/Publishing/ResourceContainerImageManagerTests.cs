// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIRECSHARPAPPS001

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Publishing;

[Trait("Partition", "4")]
public class ResourceContainerImageBuilderTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerImageBuild)]
    public async Task CanBuildArchiveFromFileBasedCSharpApp()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var appPath = Path.Combine(workspace.WorkspaceRoot.FullName, "app.cs");
        await File.WriteAllTextAsync(appPath, """
            #:property PublishAot=false

            Console.WriteLine("file app");
            """);
        var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "app.tar.gz");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddCSharpApp("file-app", appPath, options => options.ExcludeLaunchProfile = true)
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(archivePath));
        Assert.True(new FileInfo(archivePath).Length > 0);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
        Assert.Contains(logs, log => log.Message.Contains("--property:ContainerBaseImage=mcr.microsoft.com/dotnet/sdk:8.0-alpine"));
        Assert.Contains(logs, log => log.Message.Contains(".NET CLI completed with exit code: 0"));
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CanBuildImageFromDockerfileResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CanBuildImageFromDockerfileResource_WithCustomOutputPath()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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

        AssertImageArchiveWasWritten(container.Resource, tempOutputPath);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CanBuildImageFromDockerfileResource_WithAllOptionsSet()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        using var workspace = TemporaryWorkspace.Create(output);
        var tempOutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NewFolder"); // tests that the folder is created if it doesn't exist
        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
            .WithContainerBuildOptions(ctx =>
            {
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = tempOutputPath;
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

        AssertImageArchiveWasWritten(container.Resource, tempOutputPath);
    }

    [Theory]
    [InlineData(ContainerImageFormat.Docker)]
    [InlineData(ContainerImageFormat.Oci)]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CanBuildImageFromDockerfileResource_WithTrailingSlashContextPath()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        // Add trailing slashes to context path to test normalization
        var contextPathWithTrailingSlash = tempContextPath + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;
        var dockerfileResource = builder.AddDockerfile("test-dockerfile", contextPathWithTrailingSlash, tempDockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImagesAsync([dockerfileResource.Resource], cts.Token);

        // Verify that the fake runtime was called to build the image
        Assert.True(fakeContainerRuntime.WasBuildImageCalled);

        var buildCall = Assert.Single(fakeContainerRuntime.BuildImageCalls);

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

        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime(shouldFail: true));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
    public async Task BuildImageAsync_ProjectBuildFailureIncludesResourceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var workspace = TemporaryWorkspace.Create(output);

        var project = builder.AddResource(new ProjectResource("broken-project"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "missing.csproj")))
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = workspace.WorkspaceRoot.FullName;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() =>
            imageBuilder.BuildImageAsync(project.Resource, cts.Token));

        Assert.Contains("broken-project", exception.Message);
        Assert.Contains("missing.csproj", exception.Message);
        Assert.NotEqual(0, exception.ExitCode);
    }

    [Fact]
    public async Task BuildImageAsync_DotnetArchiveRequiresOutputPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var containerRuntime = new FakeContainerRuntime();
        builder.Services.AddFakeContainerRuntime(containerRuntime);
        var project = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj")))
            .WithContainerBuildOptions(context => context.Destination = ContainerImageDestination.Archive);
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => imageBuilder.BuildImageAsync(project.Resource));

        Assert.Equal(
            "Resource 'program' has Destination set to Archive but OutputPath is not configured. " +
            "Please set the OutputPath in the container build options.",
            exception.Message);
        Assert.Empty(processRunner.ProcessSpecs);
        Assert.False(containerRuntime.WasHealthCheckCalled);
    }

    [Fact]
    public async Task BuildImageAsync_ProjectBuildPassesResolvedContainerRuntimeAsLocalRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        var fakeContainerRuntime = new FakeContainerRuntime(name: "PODMAN");
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var workspace = TemporaryWorkspace.Create(output);

        var project = builder.AddResource(new ProjectResource("broken-project"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "missing.csproj")))
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.ImageFormat = ContainerImageFormat.Oci;
                ctx.OutputPath = workspace.WorkspaceRoot.FullName;
            });

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.DefaultTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await Assert.ThrowsAsync<ProcessFailedException>(() =>
            imageBuilder.BuildImageAsync(project.Resource, cts.Token));

        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        Assert.Contains(logs, log => log.Message.Contains("--property:LocalRegistry=Podman"));
    }

    [Fact]
    public async Task BuildImageAsync_DotnetProgramBuildEnvironmentIsScopedToEachBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime());

        var callbackCount = 0;
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithAnnotation(new TestBuildEnvironmentProvider(context =>
            {
                callbackCount++;
                context.EnvironmentVariables["BUILD_FLAVOR"] = $"value-{callbackCount}";
                return Task.CompletedTask;
            }))
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Oci;
                context.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource);
        await imageBuilder.BuildImageAsync(resource.Resource);

        Assert.Equal(2, callbackCount);
        Assert.Collection(
            processRunner.ProcessSpecs,
            first => AssertPublishProcess(first, "value-1"),
            second => AssertPublishProcess(second, "value-2"));

        static void AssertPublishProcess(ProcessSpec processSpec, string expectedValue)
        {
            Assert.Equal(Path.GetDirectoryName(processSpec.ArgumentList![1]), processSpec.WorkingDirectory);
            Assert.Equal(expectedValue, processSpec.EnvironmentVariables["BUILD_FLAVOR"]);
            var responseFileArgument = Assert.Single(
                processSpec.ArgumentList,
                static argument => argument.StartsWith('@'));
            Assert.False(File.Exists(responseFileArgument[1..]));
        }
    }

    [Fact]
    public async Task BuildImageAsync_DotnetProgramWithContainerFilesLayersExplicitArchive()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(output: ["/app"]);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var containerRuntime = new FakeContainerRuntime(name: "Docker");
        builder.Services.AddFakeContainerRuntime(containerRuntime);

        var source = builder.AddContainer("assets", "assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
        var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar.gz");
        File.WriteAllText(archivePath, "previous archive");
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = source.Resource,
                DestinationPath = "/app/assets"
            })
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        containerRuntime.BuildImageAsyncCallback = (contextPath, _, options, _, _, _, _) =>
        {
            Assert.Equal(workspace.WorkspaceRoot.FullName, contextPath);
            Assert.NotNull(options);
            Assert.Equal(ContainerImageDestination.Archive, options.Destination);
            Assert.Equal(ContainerImageFormat.Docker, options.ImageFormat);
            Assert.NotEqual(archivePath, options.OutputPath);
            Assert.True(Directory.Exists(options.OutputPath));

            var stagedArchivePath = ResourceExtensions.GetContainerImageArchivePath(
                options.OutputPath!,
                options.ImageName!,
                options.Tag);
            File.WriteAllText(stagedArchivePath, "archive");
            return Task.CompletedTask;
        };
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource);

        Assert.True(File.Exists(archivePath));
        Assert.Equal<byte>([0x1f, 0x8b], File.ReadAllBytes(archivePath)[..2]);
        IValueProvider imageReference = new ContainerImageReference(resource.Resource);
        var resolvedArchivePath = await imageReference.GetValueAsync(
            new ValueProviderContext
            {
                ExecutionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>()
            },
            CancellationToken.None);
        Assert.Equal(archivePath, resolvedArchivePath);
        Assert.Collection(
            processRunner.ProcessSpecs,
            publish => Assert.DoesNotContain(
                publish.ArgumentList!,
                static argument => argument.StartsWith("--property:ContainerArchiveOutputPath=", StringComparison.Ordinal)),
            workingDirectory => Assert.Contains(
                "-getProperty:ContainerWorkingDirectory",
                workingDirectory.ArgumentList!));
        var tagCall = Assert.Single(containerRuntime.TagImageCalls);
        Assert.Equal("program", tagCall.localImageName);
        Assert.StartsWith("program:temp-", tagCall.targetImageName);
        Assert.Single(containerRuntime.BuildImageCalls);
        Assert.Contains(containerRuntime.RemoveImageCalls, image => image == tagCall.targetImageName);
        Assert.Contains(containerRuntime.RemoveImageCalls, image => image == "program");
    }

    [Fact]
    public async Task BuildImageAsync_FailedArchiveCompressionPreservesExistingArchive()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(output: ["/app"]);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime(name: "Docker"));

        var source = builder.AddContainer("assets", "assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
        var archivePath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar.gz");
        await File.WriteAllTextAsync(archivePath, "previous archive");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj")))
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = source.Resource,
                DestinationPath = "/app/assets"
            })
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => imageBuilder.BuildImageAsync(resource.Resource));

        Assert.Equal("previous archive", await File.ReadAllTextAsync(archivePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildImageAsync_CrossOperatingSystemFileAppAotFailureAddsGuidance(bool truncateDiagnostic)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        var outputEvents = new List<TestProcessOutput>
        {
            new(IsError: true, "error : Cross-OS native compilation is not supported.")
        };
        if (truncateDiagnostic)
        {
            outputEvents.AddRange(Enumerable.Range(0, ProcessSpec.DefaultRetainedOutputLineCount)
                .Select(index => new TestProcessOutput(IsError: false, $"Build output {index}")));
        }

        processRunner.EnqueueResult(exitCode: 1, outputEvents: outputEvents);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime());

        var targetPlatform = OperatingSystem.IsWindows()
            ? ContainerTargetPlatform.LinuxAmd64
            : ContainerTargetPlatform.WindowsAmd64;
        var resource = builder.AddResource(new ProjectResource("file-app"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "app.cs")))
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Oci;
                context.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "app.tar");
                context.TargetPlatform = targetPlatform;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(
            () => imageBuilder.BuildImageAsync(resource.Resource));

        Assert.Contains("File-based apps enable PublishAot by default.", exception.Message);
        Assert.Contains("#:property PublishAot=false", exception.Message);
        Assert.Equal(
            outputEvents.TakeLast(ProcessSpec.DefaultRetainedOutputLineCount).Select(static line => line.Value),
            exception.ProcessOutput);
        Assert.Equal(outputEvents.Count, exception.TotalProcessOutputLineCount);
        Assert.Single(processRunner.ProcessSpecs);
    }

    [Theory]
    [InlineData(true, true, "error NU1301: Unable to load the service index.")]
    [InlineData(true, true, "error CS1002: ; expected")]
    [InlineData(false, true, "error : Cross-OS native compilation is not supported.")]
    [InlineData(true, false, "error : Cross-OS native compilation is not supported.")]
    public async Task BuildImageAsync_DoesNotInferAotFailure(bool fileBased, bool crossOperatingSystem, string error)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult(exitCode: 42, error: [error]);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime());

        ContainerTargetPlatform? targetPlatform = crossOperatingSystem
            ? OperatingSystem.IsWindows() ? ContainerTargetPlatform.LinuxAmd64 : ContainerTargetPlatform.WindowsAmd64
            : OperatingSystem.IsWindows() ? ContainerTargetPlatform.WindowsAmd64
                : OperatingSystem.IsLinux() ? ContainerTargetPlatform.LinuxAmd64 : null;
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, fileBased ? "app.cs" : "app.csproj");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "app.tar");
                context.TargetPlatform = targetPlatform;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(
            () => imageBuilder.BuildImageAsync(resource.Resource));

        Assert.Equal(42, exception.ExitCode);
        Assert.Equal([error], exception.ProcessOutput);
        Assert.Equal(
            $"Failed to build container image for resource 'program' from project '{projectPath}' with exit code 42." +
            Environment.NewLine + exception.GetFormattedOutput(),
            exception.Message);
        Assert.Single(processRunner.ProcessSpecs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildImageAsync_ContainerFilesUseReleaseWorkingDirectory(bool fileBased)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(output: ["/release-app"]);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var containerRuntime = new FakeContainerRuntime(name: "Docker");
        builder.Services.AddFakeContainerRuntime(containerRuntime);

        var source = builder.AddContainer("assets", "assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, fileBased ? "app.cs" : "app.csproj");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = source.Resource,
                DestinationPath = "wwwroot"
            });
        string? dockerfile = null;
        containerRuntime.BuildImageAsyncCallback = async (_, dockerfilePath, _, _, _, _, cancellationToken) =>
        {
            dockerfile = await File.ReadAllTextAsync(dockerfilePath, cancellationToken);
        };
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource);

        Assert.Collection(
            processRunner.ProcessSpecs,
            publish => Assert.Equal(
                [
                    "publish", projectPath, "--configuration", "Release", "/t:PublishContainer",
                    "--property:ContainerRepository=program", "--property:ContainerImageTag=latest",
                    "--property:LocalRegistry=Docker", "--property:RuntimeIdentifier=linux-x64",
                    "--property:ContainerRuntimeIdentifier=linux-x64"
                ],
                publish.ArgumentList),
            query =>
            {
                Assert.Equal(Path.GetDirectoryName(projectPath), query.WorkingDirectory);
                Assert.Equal(
                    [
                        fileBased ? "build" : "msbuild", projectPath, "-p:Configuration=Release",
                        "-getProperty:ContainerWorkingDirectory", "-v:q",
                        "--property:RuntimeIdentifier=linux-x64", "--property:ContainerRuntimeIdentifier=linux-x64"
                    ],
                    query.ArgumentList);
            });
        Assert.Single(containerRuntime.BuildImageCalls);
        Assert.NotNull(dockerfile);
        await Verify(dockerfile)
            .UseParameters(fileBased)
            .ScrubLinesWithReplace(line => Regex.Replace(line, "FROM program:temp-.*", "FROM program:temp-"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildImageAsync_MultiPlatformArgumentsSurviveMsBuildParsing(bool fileBased)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        processRunner.EnqueueResult(output: ["/release-app"]);
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime(name: "Docker"));

        var source = builder.AddContainer("assets", "assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, fileBased ? "app.cs" : "app.csproj");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = source.Resource,
                DestinationPath = "wwwroot"
            })
            .WithContainerBuildOptions(context => context.TargetPlatform = ContainerTargetPlatform.AllLinux);
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        const string runtimeIdentifiersArgument = "--property:RuntimeIdentifiers=linux-x64%3Blinux-arm64";
        const string containerRuntimeIdentifiersArgument = "--property:ContainerRuntimeIdentifiers=linux-x64%3Blinux-arm64";
        Assert.Collection(
            processRunner.ProcessSpecs,
            publish => Assert.Equal(
                [
                    "publish", projectPath, "--configuration", "Release", "/t:PublishContainer",
                    "--property:ContainerRepository=program", "--property:ContainerImageTag=latest",
                    "--property:LocalRegistry=Docker", runtimeIdentifiersArgument, containerRuntimeIdentifiersArgument
                ],
                publish.ArgumentList),
            query => Assert.Equal(
                [
                    fileBased ? "build" : "msbuild", projectPath, "-p:Configuration=Release",
                    "-getProperty:ContainerWorkingDirectory", "-v:q",
                    runtimeIdentifiersArgument, containerRuntimeIdentifiersArgument
                ],
                query.ArgumentList));

        // A fake runner cannot detect MSBuild splitting a single argv entry on ';'.
        // Round-trip each command's emitted properties through a project with no SDK or restore.
        var probePath = Path.Combine(workspace.WorkspaceRoot.FullName, "properties.proj");
        await File.WriteAllTextAsync(probePath, """
            <Project>
              <PropertyGroup>
                <ContainerWorkingDirectory Condition="'$(RuntimeIdentifiers)' == 'linux-x64;linux-arm64' and '$(ContainerRuntimeIdentifiers)' == 'linux-x64;linux-arm64'">/release-app</ContainerWorkingDirectory>
              </PropertyGroup>
            </Project>
            """, TestContext.Current.CancellationToken);

        foreach (var command in processRunner.ProcessSpecs)
        {
            var propertyArguments = command.ArgumentList!
                .Where(static argument => argument.StartsWith("--property:", StringComparison.Ordinal));
            var probe = new ProcessSpec(DotnetFileAppProcess.ResolvedExecutablePath)
            {
                ArgumentList =
                [
                    "msbuild", probePath, "-nologo",
                    "-getProperty:RuntimeIdentifiers,ContainerRuntimeIdentifiers,ContainerWorkingDirectory",
                    .. propertyArguments
                ],
                WorkingDirectory = workspace.WorkspaceRoot.FullName,
                ThrowOnNonZeroReturnCode = true
            };
            var (pendingResult, processDisposable) = ProcessUtil.Run(probe);
            await using (processDisposable)
            {
                var result = await pendingResult.WaitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                Assert.Equal(0, result.ExitCode);
                using var document = JsonDocument.Parse(string.Join(Environment.NewLine, result.ProcessOutput));
                var properties = document.RootElement.GetProperty("Properties");
                Assert.Equal("linux-x64;linux-arm64", properties.GetProperty("RuntimeIdentifiers").GetString());
                Assert.Equal("linux-x64;linux-arm64", properties.GetProperty("ContainerRuntimeIdentifiers").GetString());
                Assert.Equal("/release-app", properties.GetProperty("ContainerWorkingDirectory").GetString());
            }
        }
    }

    [Fact]
    public async Task BuildImageAsync_DynamicPropertiesSurviveMsBuildParsing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var processRunner = new TestProcessRunner();
        processRunner.EnqueueResult();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        builder.Services.AddFakeContainerRuntime(new FakeContainerRuntime(name: "Docker"));

        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "app.csproj");
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "archive;100%.tar");
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.OutputPath = outputPath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        var publish = Assert.Single(processRunner.ProcessSpecs);
        var propertyArguments = publish.ArgumentList!
            .Where(static argument => argument.StartsWith("--property:", StringComparison.Ordinal))
            .ToArray();
        var outputArgument = Assert.Single(
            propertyArguments,
            static argument => argument.StartsWith("--property:ContainerArchiveOutputPath=", StringComparison.Ordinal));
        Assert.Contains("%3B", outputArgument);
        Assert.Contains("%25", outputArgument);

        // MSBuild performs a second parsing pass over property switches. Use a project with no SDK
        // or restore to verify that every emitted value survives that pass unchanged.
        var probePath = Path.Combine(workspace.WorkspaceRoot.FullName, "properties.proj");
        await File.WriteAllTextAsync(probePath, "<Project />", TestContext.Current.CancellationToken);
        var probe = new ProcessSpec(DotnetFileAppProcess.ResolvedExecutablePath)
        {
            ArgumentList =
            [
                "msbuild", probePath, "-nologo",
                "-getProperty:ContainerRepository,ContainerImageTag,LocalRegistry,ContainerArchiveOutputPath,RuntimeIdentifier,ContainerRuntimeIdentifier",
                .. propertyArguments
            ],
            WorkingDirectory = workspace.WorkspaceRoot.FullName,
            ThrowOnNonZeroReturnCode = true
        };
        var (pendingResult, processDisposable) = ProcessUtil.Run(probe);
        await using (processDisposable)
        {
            var result = await pendingResult.WaitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(string.Join(Environment.NewLine, result.ProcessOutput));
            var properties = document.RootElement.GetProperty("Properties");
            Assert.Equal("program", properties.GetProperty("ContainerRepository").GetString());
            Assert.Equal("latest", properties.GetProperty("ContainerImageTag").GetString());
            Assert.Equal("Docker", properties.GetProperty("LocalRegistry").GetString());
            Assert.Equal(outputPath, properties.GetProperty("ContainerArchiveOutputPath").GetString());
            Assert.Equal("linux-x64", properties.GetProperty("RuntimeIdentifier").GetString());
            Assert.Equal("linux-x64", properties.GetProperty("ContainerRuntimeIdentifier").GetString());
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        Assert.Equal("mysecret", fakeContainerRuntime.CapturedBuildSecrets["SECRET_ASENV"].Value);
        Assert.Equal(BuildImageSecretType.Environment, fakeContainerRuntime.CapturedBuildSecrets["SECRET_ASENV"].Type);

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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        Assert.Equal("secret-value", fakeContainerRuntime.CapturedBuildSecrets["STRING_SECRET"].Value);
        Assert.Equal(BuildImageSecretType.Environment, fakeContainerRuntime.CapturedBuildSecrets["STRING_SECRET"].Type);

        // Null parameter should resolve to null
        Assert.Null(fakeContainerRuntime.CapturedBuildSecrets["NULL_SECRET"].Value);
        Assert.Equal(BuildImageSecretType.Environment, fakeContainerRuntime.CapturedBuildSecrets["NULL_SECRET"].Type);
    }

    [Fact]
    public async Task CanResolveBuildSecretsWithFileType()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        // Create a fake container runtime to capture build secrets
        var fakeContainerRuntime = new FakeContainerRuntime(shouldFail: false);
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        // Create a temporary file to use as a file-based secret
        using var workspace = TemporaryWorkspace.Create(output);
        var tempSecretFile = System.IO.Path.Combine(workspace.WorkspaceRoot.FullName, ".npmrc");
        await File.WriteAllTextAsync(tempSecretFile, "secret-file-content");

        // Add an env-based secret parameter
        builder.Configuration["Parameters:envsecret"] = "env-secret-value";
        var envSecret = builder.AddParameter("envsecret", secret: true);

        var container = builder.AddDockerfile("container", tempContextPath, tempDockerfilePath)
                               .WithBuildSecret("ENV_SECRET", envSecret);

        // Add a file-based secret directly via the annotation
        var annotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        annotation.BuildSecrets["FILE_SECRET"] = new FileInfo(tempSecretFile);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageBuilder.BuildImageAsync(container.Resource, cts.Token);

        // Verify that both secret types are resolved correctly
        Assert.NotNull(fakeContainerRuntime.CapturedBuildSecrets);
        Assert.Equal(2, fakeContainerRuntime.CapturedBuildSecrets.Count);

        // Environment-based secret
        Assert.Equal("env-secret-value", fakeContainerRuntime.CapturedBuildSecrets["ENV_SECRET"].Value);
        Assert.Equal(BuildImageSecretType.Environment, fakeContainerRuntime.CapturedBuildSecrets["ENV_SECRET"].Type);

        // File-based secret should resolve to the full file path
        Assert.Equal(new FileInfo(tempSecretFile).FullName, fakeContainerRuntime.CapturedBuildSecrets["FILE_SECRET"].Value);
        Assert.Equal(BuildImageSecretType.File, fakeContainerRuntime.CapturedBuildSecrets["FILE_SECRET"].Type);
    }

    [Fact]
    public void BuildSecretsStringFormatsEnvSecretCorrectly()
    {
        var secrets = new Dictionary<string, BuildImageSecretValue>
        {
            ["MY_SECRET"] = new BuildImageSecretValue("secret-value", BuildImageSecretType.Environment)
        };

        var result = ContainerRuntimeBase<DockerContainerRuntime>.BuildSecretsString(secrets);

        Assert.Equal(" --secret \"id=MY_SECRET,type=env,env=MY_SECRET\"", result);
    }

    [Fact]
    public void BuildSecretsStringFormatsFileSecretCorrectly()
    {
        var secrets = new Dictionary<string, BuildImageSecretValue>
        {
            ["npmrc"] = new BuildImageSecretValue("/path/to/.npmrc", BuildImageSecretType.File)
        };

        var result = ContainerRuntimeBase<DockerContainerRuntime>.BuildSecretsString(secrets);

        Assert.Equal(" --secret \"id=npmrc,type=file,src=/path/to/.npmrc\"", result);
    }

    [Fact]
    public void BuildSecretsStringFormatsNullEnvSecretWithRequireValue()
    {
        var secrets = new Dictionary<string, BuildImageSecretValue>
        {
            ["MY_SECRET"] = new BuildImageSecretValue(null, BuildImageSecretType.Environment)
        };

        var result = ContainerRuntimeBase<DockerContainerRuntime>.BuildSecretsString(secrets, requireValue: true);

        Assert.Equal(" --secret \"id=MY_SECRET,type=env\"", result);
    }

    [Fact]
    public void BuildSecretsStringFormatsMixedSecretTypes()
    {
        var secrets = new Dictionary<string, BuildImageSecretValue>
        {
            ["ENV_TOKEN"] = new BuildImageSecretValue("token-value", BuildImageSecretType.Environment),
            ["npmrc"] = new BuildImageSecretValue("/app/.npmrc", BuildImageSecretType.File)
        };

        var result = ContainerRuntimeBase<DockerContainerRuntime>.BuildSecretsString(secrets);

        Assert.Equal(" --secret \"id=ENV_TOKEN,type=env,env=ENV_TOKEN\" --secret \"id=npmrc,type=file,src=/app/.npmrc\"", result);
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

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddDockerfile("mycontainer", tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        // Pass a publish-mode context: the AddDockerfile default only applies linux/amd64 for publish.
        var publishContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);
        var context = await container.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            publishContext,
            CancellationToken.None);

        var dockerfileBuildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        var expectedImageTag = dockerfileBuildAnnotation.ImageTag;

        Assert.Equal("mycontainer", context.LocalImageName);
        Assert.Equal(expectedImageTag, context.LocalImageTag);
        Assert.Equal(ContainerTargetPlatform.LinuxAmd64, context.TargetPlatform);
    }

    [Fact]
    public async Task DockerfileResource_RunMode_DefaultLeavesPlatformUnset()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(output);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var container = builder.AddDockerfile("mycontainer", tempDockerfileContext.ContextPath, tempDockerfileContext.DockerfilePath);

        using var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<ResourceContainerImageBuilderTests>>();
        var runContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var context = await container.Resource.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            runContext,
            CancellationToken.None);

        Assert.Equal("mycontainer", context.LocalImageName);
        Assert.NotNull(context.LocalImageTag);
        Assert.Null(context.TargetPlatform);
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

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var workspace = TemporaryWorkspace.Create(output);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "archives");
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(output);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

        using var workspace = TemporaryWorkspace.Create(output);

        // Add two projects: one with Archive destination, one without
        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithContainerBuildOptions(ctx =>
            {
                ctx.Destination = ContainerImageDestination.Archive;
                ctx.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "archives");
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
        builder.Services.AddFakeContainerRuntime(fakeContainerRuntime);

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

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task ContainerBuildFailureIncludesProcessOutputInException()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(output);

        builder.Services.AddLogging(logging =>
        {
            logging.AddXunit(output);
        });

        // Create a Dockerfile that will fail — references a nonexistent file
        using var workspace = TemporaryWorkspace.Create(output);
        var dockerfilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, """
            FROM scratch
            COPY nonexistent-file-12345.txt /app/
            """);

        var container = builder.AddDockerfile("broken-container", workspace.WorkspaceRoot.FullName, dockerfilePath);

        using var app = builder.Build();

        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var ex = await Assert.ThrowsAsync<ProcessFailedException>(
            () => imageBuilder.BuildImageAsync(container.Resource, cts.Token));

        Assert.NotEqual(0, ex.ExitCode);
        Assert.NotEmpty(ex.ProcessOutput);

        // Each runtime prefixes the failure with its own display name ("Docker build failed…",
        // "Podman build failed…"), so resolve whichever one actually ran.
        var containerRuntime = await app.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(cts.Token);

        var newlineIndex = ex.Message.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        Assert.NotEqual(-1, newlineIndex);
        Assert.Equal($"{containerRuntime.Name} build failed with exit code {ex.ExitCode}.", ex.Message[..newlineIndex]);
        Assert.Equal(ex.GetFormattedOutput(), ex.Message[(newlineIndex + Environment.NewLine.Length)..]);
    }

    /// <summary>
    /// Asserts that the container runtime actually produced an image archive at the path consumers resolve.
    /// </summary>
    /// <remarks>
    /// Log-only assertions are not enough here: Podman used to pass <c>--output</c> to <c>podman build</c>,
    /// which is a *filesystem* export rather than an image archive, so the build "succeeded" without ever
    /// writing the archive. Checking the file also pins the Docker/Podman archive-path agreement at runtime
    /// rather than only through a string comparison in <c>PodmanSaveArgumentsTests</c>.
    /// </remarks>
    private static void AssertImageArchiveWasWritten(IResource resource, string outputPath)
    {
        Assert.True(resource.TryGetContainerImageName(out var builtImageName));

        var expectedArchivePath = ResourceExtensions.GetContainerImageArchivePath(outputPath, builtImageName);

        // Only a bounded sample of archives is listed, because one of the callers writes into the shared
        // system temp directory.
        var actualArchives = Directory.Exists(outputPath)
            ? string.Join(", ", Directory.EnumerateFiles(outputPath, "*.tar").Select(Path.GetFileName).Take(10))
            : "<output directory does not exist>";

        Assert.True(
            File.Exists(expectedArchivePath),
            $"Expected an image archive at '{expectedArchivePath}'. '{outputPath}' contains: {actualArchives}.");
    }
}

file sealed class TestProjectMetadata(string projectPath) : IProjectMetadata
{
    public string ProjectPath { get; } = projectPath;
}

file sealed class TestBuildEnvironmentProvider(
    Func<EnvironmentCallbackContext, Task> callback) : IDotnetProgramBuildEnvironmentProvider
{
    public Task ApplyAsync(EnvironmentCallbackContext context) => callback(context);

    public LaunchSettings LaunchSettings { get; } = new();
}
