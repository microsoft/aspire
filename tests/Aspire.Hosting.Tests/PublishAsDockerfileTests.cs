// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

public class PublishAsDockerfileTests
{
    [Fact]
    public async Task PublishAsDockerFileConfiguresManifestWithoutBuildArgs()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();

        var path = tempDir.Path;

        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .PublishAsDockerFile();

        // There should be an equivalent container resource with the same name
        // as the npm app resource.
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);

        var manifest = await ManifestUtils.GetManifest(frontend.Resource, manifestDirectory: path).DefaultTimeout();

        var expected =
            $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile"
              },
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}"
              }
            }
            """;

        var actual = manifest.ToString();

        Assert.Equal(expected, actual, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public async Task PublishAsDockerFileConfiguresManifestWithBuildArgs()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();

        var path = tempDir.Path;

#pragma warning disable CS0618 // Type or member is obsolete
        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .PublishAsDockerFile(buildArgs: [
                new DockerBuildArg("SOME_STRING", "Test"),
                new DockerBuildArg("SOME_BOOL", true),
                new DockerBuildArg("SOME_OTHER_BOOL", false),
                new DockerBuildArg("SOME_NUMBER", 7),
                new DockerBuildArg("SOME_NONVALUE"),
            ]);
#pragma warning restore CS0618 // Type or member is obsolete

        // There should be an equivalent container resource with the same name
        // as the npm app resource.
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);

        var manifest = await ManifestUtils.GetManifest(frontend.Resource, manifestDirectory: path).DefaultTimeout();

        var expected =
            $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "args": {
                  "SOME_STRING": "Test",
                  "SOME_BOOL": "true",
                  "SOME_OTHER_BOOL": "false",
                  "SOME_NUMBER": "7",
                  "SOME_NONVALUE": null
                }
              },
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}"
              }
            }
            """;

        var actual = manifest.ToString();

        Assert.Equal(expected, actual, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public async Task PublishAsDockerFileConfiguresManifestWithBuildArgsThatHaveNoValue()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();

        var path = tempDir.Path;

#pragma warning disable CS0618 // Type or member is obsolete
        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .PublishAsDockerFile(buildArgs: [
                new DockerBuildArg("SOME_ARG")
            ]);
#pragma warning restore CS0618 // Type or member is obsolete

        // There should be an equivalent container resource with the same name
        // as the npm app resource.
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);

        var manifest = await ManifestUtils.GetManifest(frontend.Resource, manifestDirectory: path).DefaultTimeout();

        var expected =
            $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "args": {
                  "SOME_ARG": null
                }
              },
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}"
              }
            }
            """;

        var actual = manifest.ToString();

        Assert.Equal(expected, actual, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public async Task PublishAsDockerFileConfigureContainer()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();

        var path = tempDir.Path;

        var secret = builder.AddParameter("secret", secret: true);

        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .WithArgs("/usr/foo")
            .PublishAsDockerFile(c =>
            {
                c.WithBuildSecret("buildSecret", secret);
                c.WithArgs("/app");
                c.WithVolume("vol", "/app/node_modules");
            });

        // There should be an equivalent container resource with the same name
        // as the npm app resource.
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);

        var manifest = await ManifestUtils.GetManifest(frontend.Resource, manifestDirectory: path).DefaultTimeout();

        var expected =
            $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "secrets": {
                  "buildSecret": {
                    "type": "env",
                    "value": "{secret.value}"
                  }
                }
              },
              "args": [
                "/app"
              ],
              "volumes": [
                {
                  "name": "vol",
                  "target": "/app/node_modules",
                  "readOnly": false
                }
              ],
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}"
              }
            }
            """;

        var actual = manifest.ToString();

        Assert.Equal(expected, actual, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public async Task PublishProjectAsDockerFile()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();

        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                            .WithArgs("/usr/foo")
                            .PublishAsDockerFile(c =>
                             {
                                 c.WithBuildArg("X", "y");
                                 c.WithArgs("/app");
                                 c.WithVolume("vol", "/app/shared");
                             });
        // There should be an equivalent container resource with the same name
        // as the project resource.
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("project", containerResource.Name);

        var manifest = await ManifestUtils.GetManifest(project.Resource, manifestDirectory: path).DefaultTimeout();

        var expected =
            $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "args": {
                  "X": "y"
                }
              },
              "args": [
                "/app"
              ],
              "volumes": [
                {
                  "name": "vol",
                  "target": "/app/shared",
                  "readOnly": false
                }
              ],
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory"
              }
            }
            """;

        var actual = manifest.ToString();
        Assert.Equal(expected, actual, ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void PublishProjectAsDockerFile_NoExistingEndpoints_DoesNotAddDefaultEndpoints()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                              .PublishAsDockerFile();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        // No endpoints should have been created since createIfNotExists=false and the project had none.
        Assert.Empty(container.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public void PublishProjectAsDockerFile_ExistingHttpEndpointWithoutTargetPort_SetsTargetPortTo8080()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                             .WithHttpEndpoint()
                             .PublishAsDockerFile();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("http", endpoint.Name);
        Assert.Equal(8080, endpoint.TargetPort); // TargetPort defaulted to 8080 by PublishAsDockerFile
    }

    [Fact]
    public void PublishProjectAsDockerFile_ExistingHttpEndpointWithTargetPort_PreservesTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                             .WithEndpoint("http", e =>
                             {
                                 e.UriScheme = "http";
                                 e.TargetPort = 5005; // Explicit target port
                             })
                             .PublishAsDockerFile();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("http", endpoint.Name);
        Assert.Equal(5005, endpoint.TargetPort); // Preserved, not overwritten to 8080
    }

    [Fact]
    public void PublishProjectAsDockerFile_WithLaunchSettingsHttpAndHttps_EndpointsGetDefaultTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject<TestProjectWithHttpAndHttpsProfile>("project", o => o.LaunchProfileName = "https")
                             .PublishAsDockerFile();

        // Container resource produced
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());

        var endpoints = container.Annotations.OfType<EndpointAnnotation>().OrderBy(e => e.Name).ToList();

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("http", e.Name);
                Assert.Equal(8080, e.TargetPort);
            },
            e =>
            {
                Assert.Equal("https", e.Name);
                Assert.Equal(8080, e.TargetPort);
            });
    }

    [Fact]
    public void PublishAsDockerFile_CalledMultipleTimes_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;

        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .PublishAsDockerFile()
            .PublishAsDockerFile(); // Call again - should not throw

        // There should be an equivalent container resource with the same name
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);
    }

    [Fact]
    public void PublishAsDockerFile_CalledMultipleTimesWithCallbacks_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;

        var callbackCount = 0;
        var frontend = builder.AddJavaScriptApp("frontend", path, "watch")
            .PublishAsDockerFile(c =>
            {
                callbackCount++;
                c.WithBuildArg("ARG1", "value1");
            })
            .PublishAsDockerFile(c =>
            {
                callbackCount++;
                c.WithBuildArg("ARG2", "value2");
            });

        // There should be an equivalent container resource with the same name
        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("frontend", containerResource.Name);
        
        // Both callbacks should have been invoked
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public void PublishProjectAsDockerFile_CalledMultipleTimes_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
            .PublishAsDockerFile()
            .PublishAsDockerFile(); // Call again - should not throw

        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("project", containerResource.Name);
    }

    [Fact]
    public void PublishProjectAsDockerFile_CalledMultipleTimesWithCallbacks_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var tempDir = CreateDirectoryWithDockerFile();
        var path = tempDir.Path;

        var projectPath = Path.Combine(path, "project.csproj");

        var callbackCount = 0;
        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
            .PublishAsDockerFile(c =>
            {
                callbackCount++;
                c.WithBuildArg("ARG1", "value1");
            })
            .PublishAsDockerFile(c =>
            {
                callbackCount++;
                c.WithBuildArg("ARG2", "value2");
            });

        var containerResource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("project", containerResource.Name);
        
        // Both callbacks should have been invoked
        Assert.Equal(2, callbackCount);
    }

    private static TestTempDirectory CreateDirectoryWithDockerFile()
    {
        var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Join(tempDir.Path, "Dockerfile"), "this does not matter");
        return tempDir;
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "another-path";

        public LaunchSettings? LaunchSettings { get; set; }
    }

    private sealed class TestProjectWithHttpAndHttpsProfile : IProjectMetadata
    {
        public string ProjectPath => "/foo/another-path";
        public LaunchSettings? LaunchSettings => new()
        {
            Profiles = new()
            {
                ["https"] = new LaunchProfile
                {
                    ApplicationUrl = "http://localhost:5031;https://localhost:5033",
                    CommandName = "Project"
                }
            }
        };
    }
}
