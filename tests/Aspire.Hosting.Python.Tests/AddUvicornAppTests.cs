// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Python.Tests;

public class AddUvicornAppTests
{
    [Fact]
    public void AddUvicornApp_CreatesUvicornAppResource()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var appDirectory = Path.Combine(Path.GetTempPath(), "test-app");

        // Act
        var uvicornApp = builder.AddUvicornApp("uvicorn-app", appDirectory, "main:app");

        // Assert
        Assert.IsType<UvicornAppResource>(uvicornApp.Resource);
        Assert.Equal("uvicorn-app", uvicornApp.Resource.Name);
    }

    [Fact]
    public async Task WithUv_GeneratesDockerfileInPublishMode()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a UV-based Python project with pyproject.toml and uv.lock
        var pyprojectContent = """
            [project]
            name = "test-app"
            version = "0.1.0"
            requires-python = ">=3.12"
            dependencies = []

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"
            """;

        var uvLockContent = """
            version = 1
            requires-python = ">=3.12"
            """;

        var scriptContent = """
            print("Hello from UV project!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "pyproject.toml"), pyprojectContent);
        File.WriteAllText(Path.Combine(projectDirectory, "uv.lock"), uvLockContent);
        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        var manifestPath = Path.Combine(projectDirectory, "aspire-manifest.json");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var main = builder.AddUvicornApp("main", projectDirectory, "main.py")
            .WithUv();

        var sourceFiles = builder.AddResource(new MyFilesContainer("exe", "exe", "."))
            .PublishAsDockerFile(c =>
            {
                c.WithDockerfileBuilder(".", dockerfileContext =>
                {
                    var dockerBuilder = dockerfileContext.Builder
                        .From("scratch");
                })
                .WithImageTag("deterministc-tag");
            })
            .WithAnnotation(new ContainerFilesSourceAnnotation() { SourcePath = "/app/dist" });

        main.PublishWithContainerFiles(sourceFiles, "./static");

        var app = builder.Build();

        app.Run();

        // Verify that Dockerfiles were generated for each entrypoint type
        var appDockerfilePath = Path.Combine(outputDir.Path, "main.Dockerfile");
        Assert.True(File.Exists(appDockerfilePath), "Dockerfile should be generated for script entrypoint");

        var scriptDockerfileContent = File.ReadAllText(appDockerfilePath);

        await Verify(scriptDockerfileContent);
    }

    private sealed class MyFilesContainer(string name, string command, string workingDirectory)
        : ExecutableResource(name, command, workingDirectory), IResourceWithContainerFiles;
}
