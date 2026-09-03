// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class PublishAsDockerfileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PublishAsDockerFileConfiguresManifestWithoutBuildArgs()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();

        var path = workspace.WorkspaceRoot.FullName;

        var frontend = builder.AddJavaScriptApp("frontend", path)
            .PublishAsDockerFile();

        Assert.Collection(builder.Resources, resource => Assert.Same(frontend.Resource, resource));
        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
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

        using var workspace = CreateDirectoryWithDockerFile();

        var path = workspace.WorkspaceRoot.FullName;

#pragma warning disable CS0618 // Type or member is obsolete
        var frontend = builder.AddJavaScriptApp("frontend", path)
            .PublishAsDockerFile(buildArgs: [
                new DockerBuildArg("SOME_STRING", "Test"),
                new DockerBuildArg("SOME_BOOL", true),
                new DockerBuildArg("SOME_OTHER_BOOL", false),
                new DockerBuildArg("SOME_NUMBER", 7),
                new DockerBuildArg("SOME_NONVALUE"),
            ]);
#pragma warning restore CS0618 // Type or member is obsolete

        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
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

        using var workspace = CreateDirectoryWithDockerFile();

        var path = workspace.WorkspaceRoot.FullName;

#pragma warning disable CS0618 // Type or member is obsolete
        var frontend = builder.AddJavaScriptApp("frontend", path)
            .PublishAsDockerFile(buildArgs: [
                new DockerBuildArg("SOME_ARG")
            ]);
#pragma warning restore CS0618 // Type or member is obsolete

        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
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

        using var workspace = CreateDirectoryWithDockerFile();

        var path = workspace.WorkspaceRoot.FullName;

        var secret = builder.AddParameter("secret", secret: true);

        var frontend = builder.AddJavaScriptApp("frontend", path)
            .WithArgs("/usr/foo")
            .PublishAsDockerFile(c =>
            {
                c.WithBuildSecret("buildSecret", secret);
                c.WithArgs("/app");
                c.WithVolume("vol", "/app/node_modules");
            });

        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
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
    public async Task ProjectedManifestCallbacksReceiveOwnerResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var executable = builder.AddExecutable("worker", "worker", path);
        IResource? argumentResource = null;
        IResource? environmentResource = null;
        IResource? dockerfileResource = null;

        executable
            .WithArgs(context =>
            {
                argumentResource = context.Resource;
                context.Args.Add("--projected");
            })
            .WithEnvironment(context =>
            {
                environmentResource = context.Resource;
                context.EnvironmentVariables["PROJECTED"] = "true";
            })
#pragma warning disable ASPIREDOCKERFILEBUILDER001
            .PublishAsDockerFile(container => container.WithDockerfileBuilder(path, context =>
            {
                dockerfileResource = context.Resource;
                context.Builder.From("scratch");
            }));
#pragma warning restore ASPIREDOCKERFILEBUILDER001

        await ManifestUtils.GetManifest(executable.Resource, manifestDirectory: path).DefaultTimeout();

        Assert.Same(executable.Resource, argumentResource);
        Assert.Same(executable.Resource, environmentResource);
        Assert.Same(executable.Resource, dockerfileResource);
    }

    [Fact]
    public async Task PublishProjectAsDockerFile()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();

        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                            .WithArgs("/usr/foo")
                            .PublishAsDockerFile(c =>
                             {
                                 c.WithBuildArg("X", "y");
                                 c.WithArgs("/app");
                                 c.WithVolume("vol", "/app/shared");
                             });
        Assert.Collection(builder.Resources, resource => Assert.Same(project.Resource, resource));
        var containerResource = GetContainerConfiguredOwner(project.Resource);
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

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                              .PublishAsDockerFile();

        var container = GetContainerConfiguredOwner(project.Resource);
        // No endpoints should have been created since createIfNotExists=false and the project had none.
        Assert.Empty(container.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public void PublishProjectAsDockerFile_ExistingHttpEndpointWithoutTargetPort_SetsTargetPortTo8080()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                             .WithHttpEndpoint()
                             .PublishAsDockerFile();

        var container = GetContainerConfiguredOwner(project.Resource);
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("http", endpoint.Name);
        Assert.Equal(8080, endpoint.TargetPort); // TargetPort defaulted to 8080 by PublishAsDockerFile
    }

    [Fact]
    public void PublishProjectAsDockerFile_ExistingHttpEndpointWithTargetPort_PreservesTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
                             .WithEndpoint("http", e =>
                             {
                                 e.UriScheme = "http";
                                 e.TargetPort = 5005; // Explicit target port
                             })
                             .PublishAsDockerFile();

        var container = GetContainerConfiguredOwner(project.Resource);
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("http", endpoint.Name);
        Assert.Equal(5005, endpoint.TargetPort); // Preserved, not overwritten to 8080
    }

    [Fact]
    public void PublishProjectAsDockerFile_WithLaunchSettingsHttpAndHttps_EndpointsGetDefaultTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject<TestProjectWithHttpAndHttpsProfile>("project", o => o.LaunchProfileName = "https")
                             .PublishAsDockerFile();

        var container = GetContainerConfiguredOwner(project.Resource);

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

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;

        var frontend = builder.AddJavaScriptApp("frontend", path)
            .PublishAsDockerFile()
            .PublishAsDockerFile(); // Call again - should not throw

        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
        Assert.Equal("frontend", containerResource.Name);
    }

    [Fact]
    public void PublishAsDockerFile_CalledMultipleTimesWithCallbacks_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;

        var callbackCount = 0;
        var frontend = builder.AddJavaScriptApp("frontend", path)
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

        var containerResource = GetContainerConfiguredOwner(frontend.Resource);
        Assert.Equal("frontend", containerResource.Name);
        
        // Both callbacks should have been invoked
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task PublishExecutableAsDockerFile_CalledMultipleTimes_PreservesExistingArgumentBehavior()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;

        var executable = builder.AddExecutable("worker", "worker", path)
            .WithArgs("before")
            .PublishAsDockerFile(container => container.WithArgs("first"))
            .WithArgs("between")
            .PublishAsDockerFile(container => container.WithArgs("second"))
            .WithArgs("after");

        var containerResource = GetContainerConfiguredOwner(executable.Resource);

        // Executable conversion historically clears on every call. The second clear removes the owner argument,
        // the first callback's argument, and the argument registered between calls.
        Assert.Equal(["second", "after"], await ArgumentEvaluator.GetArgumentListAsync(containerResource));
    }

    [Fact]
    public async Task PublishProjectAsDockerFile_CalledMultipleTimes_PreservesExistingArgumentBehavior()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;
        var projectPath = Path.Combine(path, "project.csproj");

        var project = builder.AddProject("project", projectPath, o => o.ExcludeLaunchProfile = true)
            .WithArgs("before")
            .PublishAsDockerFile(container => container.WithArgs("first"))
            .WithArgs("between")
            .PublishAsDockerFile(container => container.WithArgs("second"))
            .WithArgs("after");

        var containerResource = GetContainerConfiguredOwner(project.Resource);

        // Project conversion historically clears only on its first call. Arguments registered by or after that
        // call therefore remain when the resource is converted again.
        Assert.Equal(["first", "between", "second", "after"], await ArgumentEvaluator.GetArgumentListAsync(containerResource));
    }

    [Fact]
    public void PublishProjectAsDockerFile_CalledMultipleTimesWithCallbacks_IsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();
        var path = workspace.WorkspaceRoot.FullName;

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

        var containerResource = GetContainerConfiguredOwner(project.Resource);
        Assert.Equal("project", containerResource.Name);
        
        // Both callbacks should have been invoked
        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public void WithDockerfilePreservesUnrelatedPipelineStepsAndIsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var container = builder.AddContainer("api", "api:latest");

        container.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "custom",
            Resource = container.Resource,
            Action = _ => Task.CompletedTask
        });

        container
            .WithDockerfile(".")
            .WithDockerfile(".");

        // PipelineStepAnnotation supports multiple independent factories. WithDockerfile must own and replace only
        // its build/push factory rather than deleting the caller's factory or appending another copy of its own.
        Assert.Equal(2, container.Resource.Annotations.OfType<PipelineStepAnnotation>().Count());
        Assert.Single(container.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
    }

    [Fact]
    public void WithBuildArgWithoutDockerfileIncludesResourceName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var container = builder.AddContainer("api", "api:latest");

        var exception = Assert.Throws<InvalidOperationException>(() => container.WithBuildArg("ARG1", "value1"));

        Assert.Equal("The resource 'api' does not have a Dockerfile build annotation. Call WithDockerfile before calling WithBuildArg.", exception.Message);
    }

    [Fact]
    public void WithBuildArgWithSecretParameterIncludesResourceName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = CreateDirectoryWithDockerFile();

        var secret = builder.AddParameter("secret-param", secret: true);
        var container = builder.AddContainer("api", "api:latest")
            .WithDockerfile(workspace.WorkspaceRoot.FullName);

        var exception = Assert.Throws<InvalidOperationException>(() => container.WithBuildArg("ARG1", secret));

        Assert.Equal("Cannot add secret parameter 'secret-param' as build argument 'ARG1' while configuring resource 'api'. Use WithBuildSecret instead.", exception.Message);
    }

    [Fact]
    public void WithBuildSecretWithoutDockerfileIncludesResourceName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var secret = builder.AddParameter("secret-param", secret: true);
        var container = builder.AddContainer("api", "api:latest");

        var exception = Assert.Throws<InvalidOperationException>(() => container.WithBuildSecret("SECRET1", secret));

        Assert.Equal("The resource 'api' does not have a Dockerfile build annotation. Call WithDockerfile before calling WithBuildSecret.", exception.Message);
    }

    [Fact]
    public async Task ManifestPublishingProjectWithoutMetadataIncludesResourceName()
    {
        var project = new ProjectResource("project-without-metadata");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(project));

        Assert.Equal("Project metadata was not found for resource 'project-without-metadata'.", exception.Message);
    }

    [Fact]
    public async Task ManifestPublishingContainerWithoutImageNameIncludesResourceName()
    {
        var container = new ContainerResource("container-without-image");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(container));

        Assert.Equal("Could not get the container image name for resource 'container-without-image'.", exception.Message);
    }

    private TemporaryWorkspace CreateDirectoryWithDockerFile()
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Join(workspace.Path, "Dockerfile"), "this does not matter");
        return workspace;
    }

    private static IResource GetContainerConfiguredOwner(IResource owner)
    {
        Assert.Same(owner, owner.GetOwnerOrSelf());
        var projection = Assert.Single(owner.Annotations.OfType<ContainerResourceProjectionAnnotation>());
        Assert.Same(owner, projection.Projection.GetOwnerOrSelf());
        Assert.True(owner.IsContainer());
        return owner;
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