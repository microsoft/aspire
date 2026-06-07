// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Cpp.Tests;

public class AddCMakeAppTests
{
    [Fact]
    public async Task VerifyManifest_CMakeApp()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddCMakeApp("api", AppContext.BaseDirectory, "api")
            .WithHttpEndpoint(port: 8080, env: "PORT");

        var manifest = await ManifestUtils.GetManifest(app.Resource);
        var command = app.Resource.Command.Replace('\\', '/');

        var expected = $$"""
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "{{command}}",
              "env": {
                "PORT": "{api.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 8080,
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expected, manifest.ToString().Replace('\\', '/'));
    }

    [Fact]
    public async Task ConfigureResourceUsesManagedOutputDirectories()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        var configureBuilder = GetConfigureBuilder(app.Resource);
        var args = await ArgumentEvaluator.GetArgumentListAsync(configureBuilder.Resource);

        Assert.Contains("-S", args);
        Assert.Contains(builder.AppHostDirectory, args);
        Assert.Contains("-B", args);
        Assert.Contains(app.Resource.BuildDirectory, args);
        Assert.Contains("-DCMAKE_BUILD_TYPE=Debug", args);
        Assert.Contains($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY={app.Resource.RuntimeOutputDirectory}", args);
        Assert.Contains($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_DEBUG={app.Resource.RuntimeOutputDirectory}", args);
        Assert.Contains($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE={app.Resource.RuntimeOutputDirectory}", args);
    }

    [Fact]
    public async Task ConfigureResourceAppendsConfigureArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithConfigureArgs("-G", "Ninja", "-DCMAKE_TOOLCHAIN_FILE=../vcpkg/scripts/buildsystems/vcpkg.cmake");

        var configureBuilder = GetConfigureBuilder(app.Resource);
        var args = await ArgumentEvaluator.GetArgumentListAsync(configureBuilder.Resource);

        Assert.Equal("-DCMAKE_TOOLCHAIN_FILE=../vcpkg/scripts/buildsystems/vcpkg.cmake", args[^1]);
        Assert.Contains("-G", args);
        Assert.Contains("Ninja", args);
    }

    [Fact]
    public async Task BuildResourceBuildsSelectedTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        var buildBuilder = GetBuildBuilder(app.Resource);
        var args = await ArgumentEvaluator.GetArgumentListAsync(buildBuilder.Resource);

        Assert.Equal(["--build", app.Resource.BuildDirectory, "--config", "Debug", "--target", "api"], args);
    }

    [Fact]
    public async Task BuildResourceUsesBuildTypeAndBuildArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithBuildType("RelWithDebInfo")
            .WithBuildArgs("--parallel", "4");

        var buildBuilder = GetBuildBuilder(app.Resource);
        var args = await ArgumentEvaluator.GetArgumentListAsync(buildBuilder.Resource);

        Assert.Equal(["--build", app.Resource.BuildDirectory, "--config", "RelWithDebInfo", "--target", "api", "--parallel", "4"], args);
    }

    [Fact]
    public void AddCMakeAppCreatesConfigureAndBuildSiblingsInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        var configure = GetConfigureBuilder(app.Resource);
        var build = GetBuildBuilder(app.Resource);

        Assert.Same(configure.Resource, builder.Resources.First(r => r.Name == "api-cmake-configure"));
        Assert.Same(build.Resource, builder.Resources.First(r => r.Name == "api-cmake-build"));
        Assert.Contains(build.Resource.Annotations.OfType<WaitAnnotation>(), a => a.Resource == configure.Resource && a.WaitType == WaitType.WaitForCompletion);
        Assert.Contains(app.Resource.Annotations.OfType<WaitAnnotation>(), a => a.Resource == build.Resource && a.WaitType == WaitType.WaitForCompletion);
    }

    [Fact]
    public void AddCMakeAppDoesNotCreateConfigureAndBuildSiblingsInPublishMode()
    {
        using var outputDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        Assert.False(app.Resource.TryGetLastAnnotation<CMakeConfigureResourceAnnotation>(out _));
        Assert.False(app.Resource.TryGetLastAnnotation<CMakeBuildResourceAnnotation>(out _));
    }

    [Fact]
    public async Task VerifyPublish_GeneratesDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.20)
            project(api LANGUAGES CXX)
            add_executable(api main.cpp)
            """);
        File.WriteAllText(Path.Combine(sourceDir.Path, "main.cpp"), "int main() { return 0; }");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddCMakeApp("api", sourceDir.Path, "api");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_RespectsDockerfileBaseImageAnnotation()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.20)
            project(api LANGUAGES CXX)
            add_executable(api main.cpp)
            """);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddCMakeApp("api", sourceDir.Path, "api")
            .WithDockerfileBaseImage(buildImage: "ubuntu:24.04", runtimeImage: "ubuntu:24.04");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_PropagatesConfigureAndBuildArgsToDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.20)
            project(api LANGUAGES CXX)
            add_executable(api main.cpp)
            """);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddCMakeApp("api", sourceDir.Path, "api")
            .WithConfigureArgs("-DENABLE_HTTP=ON", "-DCMAKE_TOOLCHAIN_FILE=/opt/vcpkg/scripts/buildsystems/vcpkg.cmake")
            .WithBuildArgs("--parallel", "4");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public void VerifyPublish_SkipsDockerfileGeneration_WhenDockerfileExists()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "Dockerfile"), "FROM scratch");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        var app = builder.AddCMakeApp("api", sourceDir.Path, "api");

        Assert.False(app.Resource.TryGetLastAnnotation<DockerfileBuilderCallbackAnnotation>(out _));
    }

    [Fact]
    public void CMakeAppResource_ImplementsIContainerFilesDestinationResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddCMakeApp("api", AppContext.BaseDirectory, "api");

        Assert.IsType<CMakeAppResource>(app.Resource, exactMatch: false);
        Assert.True(app.Resource is IContainerFilesDestinationResource);
    }

    private static IResourceBuilder<ExecutableResource> GetConfigureBuilder(CMakeAppResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<CMakeConfigureResourceAnnotation>(out var annotation));
        return annotation.ResourceBuilder;
    }

    private static IResourceBuilder<ExecutableResource> GetBuildBuilder(CMakeAppResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<CMakeBuildResourceAnnotation>(out var annotation));
        return annotation.ResourceBuilder;
    }
}
