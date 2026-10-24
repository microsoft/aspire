// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Internal;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

[SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "WSLc is Windows-only.")]
public class WslcContainerRuntimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CheckIfRunningAsync_UsesContainerList(int exitCode, bool expected)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        Assert.Equal(expected, await runtime.CheckIfRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal("container ls -n 1", Assert.Single(runner.ProcessSpecs).Arguments);
    }

    [Fact]
    public async Task BuildImageAsync_PassesBuildArgumentsSecretsAndStage()
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        await runtime.BuildImageAsync(
            "context",
            "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", Tag = "latest", ImageFormat = ContainerImageFormat.Docker },
            new() { ["VERSION"] = "1.0", ["INHERITED"] = null },
            new()
            {
                ["token"] = new("secret-value", BuildImageSecretType.Environment),
                ["file"] = new("secret.txt", BuildImageSecretType.File)
            },
            "final",
            TestContext.Current.CancellationToken);

        var spec = Assert.Single(runner.ProcessSpecs);
        Assert.Equal("wslc", spec.ExecutablePath);
        Assert.Equal(
            ["build", "--file", "Dockerfile", "--tag", "myapp:latest", "--build-arg", "VERSION=1.0", "--build-arg", "INHERITED",
                "--secret", "id=token,type=env,env=TOKEN", "--secret", "id=file,type=file,src=secret.txt", "--target", "final",
                Path.GetFullPath("context")],
            spec.ArgumentList);
        Assert.Equal("secret-value", spec.EnvironmentVariables["TOKEN"]);
        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Theory]
    [InlineData(ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerTargetPlatform.LinuxArm64)]
    [InlineData(ContainerTargetPlatform.AllLinux)]
    [InlineData(ContainerTargetPlatform.WindowsAmd64)]
    public async Task BuildImageAsync_RejectsSelectedPlatformBeforeStartingProcess(ContainerTargetPlatform platform)
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", TargetPlatform = platform },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support selecting a platform", exception.Message);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null, "out", null)]
    [InlineData(ContainerImageFormat.Docker, "out", null)]
    [InlineData(ContainerImageFormat.Oci, "out", null)]
    [InlineData(ContainerImageFormat.Oci, null, null)]
    [InlineData(null, null, ContainerImageDestination.Archive)]
    public async Task BuildImageAsync_RejectsArchiveAndOciOutputBeforeStartingProcess(
        ContainerImageFormat? format, string? outputPath, ContainerImageDestination? destination)
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp", ImageFormat = format, OutputPath = outputPath, Destination = destination },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support Dockerfile image archive output or OCI image format selection.", exception.Message);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_PreservesProcessFailureOutput()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode: 42, error: ["build failed"]);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() => runtime.BuildImageAsync(
            "context", "Dockerfile",
            new ContainerImageBuildOptions { ImageName = "myapp" },
            [], [], null, TestContext.Current.CancellationToken));

        Assert.Equal(42, exception.ExitCode);
        Assert.Equal(["build failed"], exception.ProcessOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeOperations_PropagateCancellationAndDisposeProcess(bool build)
    {
        var runner = new TestProcessRunner();
        runner.EnqueuePending(new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        using var cancellation = new CancellationTokenSource();

        var operation = build
            ? runtime.BuildImageAsync("context", "Dockerfile", new ContainerImageBuildOptions { ImageName = "myapp" }, [], [], null, cancellation.Token)
            : runtime.CheckIfRunningAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DockerfilePublishing_RequiresOptingOutOfDefaultPlatform(bool hostPlatform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var resource = builder.AddDockerfile("program", workspace.WorkspaceRoot.FullName);
        if (hostPlatform)
        {
            resource.WithContainerBuildOptions(options => options.TargetPlatform = null);
        }
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        if (hostPlatform)
        {
            await manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);
            Assert.Equal(2, runner.ProcessSpecs.Count);
            Assert.Equal("build", runner.ProcessSpecs[1].ArgumentList![0]);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));
            Assert.Contains("WSLc does not support selecting a platform", exception.Message);
            Assert.Single(runner.ProcessSpecs);
        }
        Assert.Equal("container ls -n 1", runner.ProcessSpecs[0].Arguments);
    }

    [Fact]
    public async Task DockerfilePublishing_RejectsArchiveDestinationWithoutOutputPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var resource = builder.AddDockerfile("program", workspace.WorkspaceRoot.FullName)
            .WithContainerBuildOptions(options =>
            {
                options.TargetPlatform = null;
                options.Destination = ContainerImageDestination.Archive;
            });
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support Dockerfile image archive output", exception.Message);
        Assert.Equal("container ls -n 1", Assert.Single(runner.ProcessSpecs).Arguments);
    }

    [Fact]
    public async Task ImageOperations_UseSharedWslcCommands()
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var resource = builder.AddContainer("myapp", "myapp", "latest").WithContainerRegistry(registry);

        await runtime.TagImageAsync("myapp:latest", "myapp:next", TestContext.Current.CancellationToken);
        await runtime.PushImageAsync(resource.Resource, TestContext.Current.CancellationToken);
        await runtime.LoginToRegistryAsync("registry.example.com", "user", "password", TestContext.Current.CancellationToken);
        await runtime.RemoveImageAsync("myapp:next", TestContext.Current.CancellationToken);

        Assert.Collection(runner.ProcessSpecs,
            spec => Assert.Equal("tag \"myapp:latest\" \"myapp:next\"", spec.Arguments),
            spec => Assert.Equal("tag \"myapp:latest\" \"registry.example.com/myapp:latest\"", spec.Arguments),
            spec => Assert.Equal("push \"registry.example.com/myapp:latest\"", spec.Arguments),
            spec =>
            {
                Assert.Equal("login --username \"user\" --password-stdin \"registry.example.com\"", spec.Arguments);
                Assert.Equal("password", spec.StandardInputContent);
            },
            spec => Assert.Equal("rmi \"myapp:next\"", spec.Arguments));
        Assert.All(runner.ProcessSpecs, spec => Assert.Equal("wslc", spec.ExecutablePath));
    }

    [Fact]
    public async Task InspectImageConfigAsync_ParsesNativeJson()
    {
        const string json = """[{"Config":{"Entrypoint":["dotnet","app.dll"],"Cmd":["--urls"],"WorkingDir":"/app"}}]""";
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [json]);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        const string imageName = "registry/image\\\" --help";

        var result = await runtime.InspectImageConfigAsync(imageName, TestContext.Current.CancellationToken);

        Assert.Equal(["image", "inspect", imageName, "--format", "json"], Assert.Single(runner.ProcessSpecs).ArgumentList);
        Assert.Equal(ContainerImageInspectionStatus.Succeeded, result.Status);
        Assert.Equal(json, result.RawJson);
        Assert.True(result.TryGetConfig(out var config));
        Assert.Equal(["dotnet", "app.dll"], config.Entrypoint);
        Assert.Equal(["--urls"], config.Command);
        Assert.Equal("/app", config.WorkingDirectory);
    }

    [Fact]
    public async Task InspectImageConfigAsync_AllowsNullEntrypointAndCommand()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: ["""[{"Config":{"Entrypoint":null,"Cmd":null,"WorkingDir":""}}]"""]);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.True(result.TryGetConfig(out var config));
        Assert.Empty(config.Entrypoint);
        Assert.Empty(config.Command);
        Assert.Equal("", config.WorkingDirectory);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Config":{}},{"Config":{}}]""")]
    [InlineData("""[{"Config":{"Cmd":42}}]""")]
    public async Task InspectImageConfigAsync_ReportsInvalidMetadata(string json)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [json]);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Equal("WSLc returned invalid image configuration for 'image'.", result.ErrorMessage);
        Assert.False(result.TryGetConfig(out _));
    }

    [Fact]
    public async Task InspectImageConfigAsync_ReportsCommandFailure()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode: 1, error: ["image not found"]);
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);

        var result = await runtime.InspectImageConfigAsync("image", TestContext.Current.CancellationToken);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.Contains("image not found", result.ErrorMessage);
    }

    [Fact]
    public async Task UnsupportedOperations_DoNotStartProcesses()
    {
        var runner = new TestProcessRunner();
        var runtime = new WslcContainerRuntime(NullLogger<WslcContainerRuntime>.Instance, runner);
        var context = new ComposeOperationContext { ProjectName = "app", WorkingDirectory = "out" };

        var up = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeUpAsync(context, TestContext.Current.CancellationToken));
        var down = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeDownAsync(context, TestContext.Current.CancellationToken));
        var list = await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeListServicesAsync(context, TestContext.Current.CancellationToken));
        Assert.Equal("WSLc does not support Docker Compose deployment or management. Set ASPIRE_CONTAINER_RUNTIME to docker or podman for Compose operations.", up.Message);
        Assert.Equal(up.Message, down.Message);
        Assert.Equal(up.Message, list.Message);

        var inspection = await runtime.InspectImageManifestAsync("image", TestContext.Current.CancellationToken);
        Assert.Equal(ContainerImageInspectionStatus.Unsupported, inspection.Status);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(ContainerTargetPlatform.AllLinux)]
    [InlineData(ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.WindowsAmd64)]
    [InlineData(ContainerTargetPlatform.WindowsAmd64 | ContainerTargetPlatform.WindowsArm64)]
    public async Task DotnetPublishing_RejectsMultiPlatformLocalStoreBeforeStartingProcess(ContainerTargetPlatform platform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var resource = builder.AddProject<Projects.ServiceA>("program")
            .WithContainerBuildOptions(options => options.TargetPlatform = platform);

        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support loading multi-platform .NET SDK images into its local image store", exception.Message);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DotnetPublishing_RejectsPlatformSelectedContainerFileLayering(bool archive)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var source = builder.AddContainer("assets", "assets-image");
        var resource = builder.AddProject<Projects.ServiceA>("program")
            .WithAnnotation(new ContainerFilesDestinationAnnotation { Source = source.Resource, DestinationPath = "wwwroot" })
            .WithContainerBuildOptions(options =>
            {
                options.TargetPlatform = null;
                if (archive)
                {
                    options.Destination = ContainerImageDestination.Archive;
                    options.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
                }
            });
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken));

        Assert.Contains("WSLc does not support the platform-selected Dockerfile build required by PublishWithContainerFiles", exception.Message);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerTargetPlatform.LinuxArm64)]
    public async Task DotnetPublishing_SinglePlatformUsesSdkWslcLocalRegistry(ContainerTargetPlatform? platform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var resource = builder.AddProject<Projects.ServiceA>("program")
            .WithContainerBuildOptions(options => options.TargetPlatform = platform);
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        Assert.Collection(runner.ProcessSpecs,
            spec =>
            {
                Assert.Equal("wslc", spec.ExecutablePath);
                Assert.Equal("container ls -n 1", spec.Arguments);
            },
            spec =>
            {
                Assert.Equal("dotnet", spec.ExecutablePath);
                var expectedArguments = new List<string>
                {
                    "publish", resource.Resource.GetProjectMetadata().ProjectPath,
                    "--configuration", "Release", "/t:PublishContainer",
                    "--property:ContainerRepository=program", "--property:ContainerImageTag=latest",
                    "--property:LocalRegistry=Wslc"
                };
                if (platform is not null)
                {
                    var rid = platform == ContainerTargetPlatform.LinuxArm64 ? "linux-arm64" : "linux-x64";
                    expectedArguments.AddRange([$"--property:RuntimeIdentifier={rid}", $"--property:ContainerRuntimeIdentifier={rid}"]);
                }
                Assert.Equal(expectedArguments, spec.ArgumentList);
            });
    }

    [Theory]
    [InlineData(null, ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerImageFormat.Docker, ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerImageFormat.Oci, ContainerTargetPlatform.LinuxAmd64)]
    [InlineData(ContainerImageFormat.Oci, ContainerTargetPlatform.AllLinux)]
    public async Task DotnetPublishing_ArchiveDoesNotInvokeWslc(ContainerImageFormat? format, ContainerTargetPlatform platform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(output);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = "wslc");
        var resource = builder.AddProject<Projects.ServiceA>("program")
            .WithContainerBuildOptions(options =>
            {
                options.Destination = ContainerImageDestination.Archive;
                options.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
                options.ImageFormat = format;
                options.TargetPlatform = platform;
            });
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await manager.BuildImagesAsync([resource.Resource], TestContext.Current.CancellationToken);

        var spec = Assert.Single(runner.ProcessSpecs);
        Assert.Equal("dotnet", spec.ExecutablePath);
        var expectedArguments = new List<string>
        {
            "publish",
            resource.Resource.GetProjectMetadata().ProjectPath,
            "--configuration",
            "Release",
            "/t:PublishContainer",
            "--property:ContainerRepository=program",
            "--property:ContainerImageTag=latest",
            MsBuildResponseFileFactory.CreatePropertyArgument("ContainerArchiveOutputPath", Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar"))
        };
        if (format is not null)
        {
            expectedArguments.Add($"--property:ContainerImageFormat={(format == ContainerImageFormat.Oci ? "OCI" : "Docker")}");
        }
        if (platform == ContainerTargetPlatform.AllLinux)
        {
            expectedArguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("RuntimeIdentifiers", "linux-x64;linux-arm64"));
            expectedArguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("ContainerRuntimeIdentifiers", "linux-x64;linux-arm64"));
        }
        else
        {
            expectedArguments.AddRange(["--property:RuntimeIdentifier=linux-x64", "--property:ContainerRuntimeIdentifier=linux-x64"]);
        }
        Assert.Equal(expectedArguments, spec.ArgumentList);
    }
}
