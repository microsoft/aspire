// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPROJECTS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

[SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "WSLc is Windows-only.")]
public class WslcDotnetPublishingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ContainerTargetPlatform.AllLinux)]
    [InlineData(ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.WindowsAmd64)]
    [InlineData(ContainerTargetPlatform.WindowsAmd64 | ContainerTargetPlatform.WindowsArm64)]
    public async Task DotnetPublishing_RejectsMultiPlatformLocalStoreBeforeStartingProcess(ContainerTargetPlatform platform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = KnownContainerRuntimes.Wslc);
        var resource = builder.AddDotnetProject("program", "program.csproj", options => options.ExcludeLaunchProfile = true)
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
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = KnownContainerRuntimes.Wslc);
        var source = builder.AddContainer("assets", "assets-image");
        var resource = builder.AddDotnetProject("program", "program.csproj", options => options.ExcludeLaunchProfile = true)
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
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = KnownContainerRuntimes.Wslc);
        var resource = builder.AddDotnetProject("program", "program.csproj", options => options.ExcludeLaunchProfile = true)
            .WithContainerBuildOptions(options => options.TargetPlatform = platform);
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        Assert.Collection(runner.ProcessSpecs,
            spec =>
            {
                Assert.Equal(KnownContainerRuntimes.Wslc, spec.ExecutablePath);
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
        builder.Services.Configure<DcpOptions>(options => options.ContainerRuntime = KnownContainerRuntimes.Wslc);
        var resource = builder.AddDotnetProject("program", "program.csproj", options => options.ExcludeLaunchProfile = true)
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
            expectedArguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("RuntimeIdentifier", string.Empty));
            expectedArguments.Add(MsBuildResponseFileFactory.CreatePropertyArgument("ContainerRuntimeIdentifier", string.Empty));
            expectedArguments.Add(MsBuildResponseFileFactory.CreateListPropertyArgument("RuntimeIdentifiers", ["linux-x64", "linux-arm64"]));
            expectedArguments.Add(MsBuildResponseFileFactory.CreateListPropertyArgument("ContainerRuntimeIdentifiers", ["linux-x64", "linux-arm64"]));
        }
        else
        {
            expectedArguments.AddRange(["--property:RuntimeIdentifier=linux-x64", "--property:ContainerRuntimeIdentifier=linux-x64"]);
        }
        Assert.Equal(expectedArguments, spec.ArgumentList);
    }
}
