// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001

using System.ComponentModel;
using System.Runtime.InteropServices;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

public class WslcContainerRuntimeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ContainerImageFormat.Docker)]
    [InlineData(ContainerImageFormat.Oci)]
    public async Task BuildImageAsync_ExportsRequestedArchiveFormat(ContainerImageFormat? format)
    {
        var runner = new TestProcessRunner();
        var runtime = CreateRuntime(runner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "app",
            Tag = "release",
            OutputPath = "out",
            ImageFormat = format
        };

        await runtime.BuildImageAsync("context", "Dockerfile", options, [], [], null, CancellationToken.None);

        var archivePath = ResourceExtensions.GetContainerImageArchivePath("out", "app:release");
        Assert.Collection(runner.ProcessSpecs,
            build => Assert.Equal(
                "image build --file \"Dockerfile\" --tag \"app:release\" --progress plain " +
                $"\"{Path.GetFullPath("context").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\"",
                build.Arguments),
            save => Assert.Equal($"image save --output \"{archivePath}\" \"app:release\"", save.Arguments));
        Assert.All(runner.ProcessSpecs, spec => Assert.Equal("wslc", spec.ExecutablePath));
    }

    [Fact]
    public async Task BuildImageAsync_OciExportRequiresOutputPath()
    {
        var runner = new TestProcessRunner();
        var options = new ContainerImageBuildOptions
        {
            ImageName = "app",
            ImageFormat = ContainerImageFormat.Oci
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateRuntime(runner).BuildImageAsync("context", "Dockerfile", options, [], [], null, CancellationToken.None));
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_PassesBuildArgumentsSecretsAndStage()
    {
        var runner = new TestProcessRunner();
        var runtime = CreateRuntime(runner);
        var options = new ContainerImageBuildOptions
        {
            ImageName = "app",
            TargetPlatform = RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? ContainerTargetPlatform.LinuxArm64
                : ContainerTargetPlatform.LinuxAmd64
        };

        await runtime.BuildImageAsync(
            "context", "Dockerfile", options,
            new() { ["FLAVOR"] = "release" },
            new() { ["token"] = new BuildImageSecretValue("secret", BuildImageSecretType.Environment) },
            "final", CancellationToken.None);

        var spec = Assert.Single(runner.ProcessSpecs);
        Assert.Equal(
            "image build --file \"Dockerfile\" --tag \"app\" --progress plain --build-arg \"FLAVOR=release\" " +
            "--secret \"id=token,type=env,env=TOKEN\" --target \"final\" " +
            $"\"{Path.GetFullPath("context").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\"",
            spec.Arguments);
        Assert.Equal("secret", spec.EnvironmentVariables["TOKEN"]);
    }

    [Theory]
    [InlineData(ContainerTargetPlatform.AllLinux)]
    [InlineData(ContainerTargetPlatform.WindowsAmd64)]
    public async Task BuildImageAsync_RejectsUnsupportedPlatforms(ContainerTargetPlatform platform)
    {
        var runner = new TestProcessRunner();
        var runtime = CreateRuntime(runner);
        var options = new ContainerImageBuildOptions { ImageName = "app", TargetPlatform = platform };

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => runtime.BuildImageAsync("context", "Dockerfile", options, [], [], null, CancellationToken.None));

        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task BuildImageAsync_PreservesFailureOutput()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode: 42, error: ["build failed"]);
        var runtime = CreateRuntime(runner);

        var exception = await Assert.ThrowsAsync<ProcessFailedException>(() =>
            runtime.BuildImageAsync("context", "Dockerfile", new() { ImageName = "app" }, [], [], null, CancellationToken.None));

        Assert.Equal(42, exception.ExitCode);
        Assert.Equal(["build failed"], exception.ProcessOutput);
        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Fact]
    public async Task LoginToRegistryAsync_PassesPasswordOnlyThroughStandardInput()
    {
        var runner = new TestProcessRunner();
        await CreateRuntime(runner).LoginToRegistryAsync("registry.example.com", "user", "secret", CancellationToken.None);

        var spec = Assert.Single(runner.ProcessSpecs);
        Assert.Equal("wslc", spec.ExecutablePath);
        Assert.Equal("login --username \"user\" --password-stdin \"registry.example.com\"", spec.Arguments);
        Assert.Equal("secret", spec.StandardInputContent);
    }

    [Fact]
    public async Task ImageOperations_UseWslcAliases()
    {
        var runner = new TestProcessRunner();
        var runtime = CreateRuntime(runner);
        await runtime.TagImageAsync("app", "app:release", CancellationToken.None);
        await runtime.RemoveImageAsync("app:release", CancellationToken.None);

        Assert.Collection(runner.ProcessSpecs,
            tag => Assert.Equal("tag \"app\" \"app:release\"", tag.Arguments),
            remove => Assert.Equal("rmi \"app:release\"", remove.Arguments));
        Assert.All(runner.ProcessSpecs, spec => Assert.Equal("wslc", spec.ExecutablePath));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CheckIfRunningAsync_UsesLiveContainerQuery(int exitCode, bool expected)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode);

        Assert.Equal(expected, await CreateRuntime(runner).CheckIfRunningAsync(CancellationToken.None));
        Assert.Equal("container ls -n 1", Assert.Single(runner.ProcessSpecs).Arguments);
    }

    [Fact]
    public async Task CheckIfRunningAsync_ReturnsFalseWhenExecutableIsMissing()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueException(new Win32Exception());

        Assert.False(await CreateRuntime(runner).CheckIfRunningAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InspectImageConfigAsync_ReadsWslcJson()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: ["""[{"Config":{"Entrypoint":["dotnet"],"Cmd":["app.dll"],"WorkingDir":"/app"}}]"""]);

        var result = await CreateRuntime(runner).InspectImageConfigAsync("app", CancellationToken.None);

        Assert.True(result.TryGetConfig(out var config));
        Assert.Equal(["dotnet"], config.Entrypoint);
        Assert.Equal(["app.dll"], config.Command);
        Assert.Equal("/app", config.WorkingDirectory);
        Assert.Equal(["image", "inspect", "app", "--format", "json"], Assert.Single(runner.ProcessSpecs).ArgumentList);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("invalid json")]
    [InlineData("""[{"Config":"invalid"}]""")]
    public async Task InspectImageConfigAsync_ReportsInvalidMetadata(string output)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [output]);
        var result = await CreateRuntime(runner).InspectImageConfigAsync("app", CancellationToken.None);

        Assert.Equal(ContainerImageInspectionStatus.Failed, result.Status);
        Assert.NotEmpty(result.ErrorMessage!);
        Assert.False(result.TryGetConfig(out _));
    }

    [Fact]
    public async Task UnsupportedOperations_DoNotLaunchCommands()
    {
        var runner = new TestProcessRunner();
        var runtime = CreateRuntime(runner);
        var context = new ComposeOperationContext { ProjectName = "app", WorkingDirectory = "context" };

        await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeUpAsync(context, CancellationToken.None));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeDownAsync(context, CancellationToken.None));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => runtime.ComposeListServicesAsync(context, CancellationToken.None));
        Assert.Equal(ContainerImageInspectionStatus.Unsupported,
            (await runtime.InspectImageManifestAsync("app", CancellationToken.None)).Status);
        Assert.Empty(runner.ProcessSpecs);
    }

    [Fact]
    public async Task SdkPublishing_PassesWslcLocalRegistryWithoutArchiveWorkaround()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Configuration["ASPIRE_CONTAINER_RUNTIME"] = "wslc";
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
#pragma warning disable CS0618 // Verify legacy ProjectResource publishing also selects the WSLC SDK image store.
        var project = builder.AddProject<Projects.ServiceA>("app");
#pragma warning restore CS0618
        using var app = builder.Build();

        Assert.IsType<WslcContainerRuntime>(await app.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync());
        await app.Services.GetRequiredService<IResourceContainerImageManager>().BuildImageAsync(project.Resource);

        Assert.Collection(runner.ProcessSpecs,
            health => Assert.Equal("container ls -n 1", health.Arguments),
            publish => Assert.Equal(
                ["publish", project.Resource.GetProjectMetadata().ProjectPath, "--configuration", "Release",
                "/t:PublishContainer", "--property:ContainerRepository=app", "--property:ContainerImageTag=latest",
                "--property:LocalRegistry=Wslc", "--property:RuntimeIdentifier=linux-x64",
                "--property:ContainerRuntimeIdentifier=linux-x64"],
                publish.ArgumentList));
    }

    private static WslcContainerRuntime CreateRuntime(TestProcessRunner runner) =>
        new(NullLogger<WslcContainerRuntime>.Instance, runner);
}
