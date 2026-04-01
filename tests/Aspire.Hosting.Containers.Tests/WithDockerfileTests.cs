// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Containers.Tests;

public class WithDockerfileTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task WithBuildSecretPopulatesSecretFilesCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(includeSecrets: true);
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.Configuration["Parameters:secret"] = "open sesame from env";
        var parameter = builder.AddParameter("secret", secret: true);

        builder.AddContainer("testcontainer", "testimage")
               .WithHttpEndpoint(targetPort: 80)
               .WithDockerfile(tempContextPath, tempDockerfilePath)
               .WithBuildSecret("ENV_SECRET", parameter);

        using var app = builder.Build();
        await app.StartAsync();

        await WaitForResourceAsync(app, "testcontainer", "Running");

        using var client = app.CreateHttpClient("testcontainer", "http");

        var envSecretMessage = await client.GetStringAsync("/ENV_SECRET.txt");
        Assert.Equal("open sesame from env", envSecretMessage);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task ContainerBuildLogsAreStreamedToAppHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(testOutputHelper);
        });

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.AddContainer("testcontainer", "testimage")
               .WithHttpEndpoint(targetPort: 80)
               .WithDockerfile(tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        await app.StartAsync();

        // Wait for the resource to come online.
        await WaitForResourceAsync(app, "testcontainer", "Running");
        using var client = app.CreateHttpClient("testcontainer", "http");
        var message = await client.GetStringAsync("/aspire.html");

        // By the time we can make a request to the service the logs
        // should be streamed back to the app host.
        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        // Just looking for a common message in Docker build output.
        Assert.Contains(logs, log => log.Message.Contains("load build definition from Dockerfile"));

        await app.StopAsync();
    }

    [Theory]
    [InlineData("testcontainer")]
    [InlineData("TestContainer")]
    [InlineData("test-Container")]
    [InlineData("TEST-234-CONTAINER")]
    public async Task AddDockerfileUsesLowercaseResourceNameAsImageName(string resourceName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var dockerFile = builder.AddDockerfile(resourceName, tempContextPath, tempDockerfilePath);

        // The effective image name (from TryGetContainerImageName) should be the lowercase resource name
        Assert.True(dockerFile.Resource.TryGetContainerImageName(out var imageName));
        Assert.StartsWith(resourceName.ToLowerInvariant() + ":", imageName);

        // The DockerfileBuildAnnotation should have the generated image name
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation));
        Assert.Equal(resourceName.ToLowerInvariant(), buildAnnotation.ImageName);
    }

    [Theory]
    [InlineData("testcontainer")]
    [InlineData("TestContainer")]
    [InlineData("test-Container")]
    [InlineData("TEST-234-CONTAINER")]
    public async Task WithDockerfileUsesLowercaseResourceNameAsImageName(string resourceName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var dockerFile = builder.AddContainer(resourceName, "someimagename")
            .WithDockerfile(tempContextPath, tempDockerfilePath);

        // After the changes, ContainerImageAnnotation should be preserved
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var containerImageAnnotation));
        Assert.Equal("someimagename", containerImageAnnotation.Image);

        // The generated image name should be stored in DockerfileBuildAnnotation
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation));
        Assert.Equal(resourceName.ToLowerInvariant(), buildAnnotation.ImageName);

        // TryGetContainerImageName should return the DockerfileBuildAnnotation image name
        Assert.True(dockerFile.Resource.TryGetContainerImageName(out var imageName));
        Assert.StartsWith(resourceName.ToLowerInvariant() + ":", imageName);
    }

    [Fact]
    public async Task WithDockerfileUsesGeneratesDifferentHashForImageTagOnEachCall()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var dockerFile = builder.AddContainer("testcontainer", "someimagename")
            .WithDockerfile(tempContextPath, tempDockerfilePath);
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation1));
        var tag1 = buildAnnotation1.ImageTag;

        dockerFile.WithDockerfile(tempContextPath, tempDockerfilePath);
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation2));
        var tag2 = buildAnnotation2.ImageTag;

        Assert.NotEqual(tag1, tag2);
    }

    [Fact]
    public async Task WithDockerfileGeneratedImageTagCanBeOverridden()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var dockerFile = builder.AddContainer("testcontainer", "someimagename")
            .WithDockerfile(tempContextPath, tempDockerfilePath);

        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation1));
        var generatedTag = buildAnnotation1.ImageTag;

        dockerFile.WithImageTag("latest");
        Assert.True(dockerFile.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var buildAnnotation2));
        var overriddenTag = buildAnnotation2.ImageTag;

        Assert.NotEqual(generatedTag, overriddenTag);
        Assert.Equal("latest", overriddenTag);

        // Verify that TryGetContainerImageName returns the overridden tag
        Assert.True(dockerFile.Resource.TryGetContainerImageName(out var imageName));
        Assert.EndsWith(":latest", imageName);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task WithDockerfileLaunchesContainerSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.AddContainer("testcontainer", "testimage")
               .WithHttpEndpoint(targetPort: 80)
               .WithDockerfile(tempContextPath, tempDockerfilePath);

        using var app = builder.Build();
        await app.StartAsync();

        await WaitForResourceAsync(app, "testcontainer", "Running");

        using var client = app.CreateHttpClient("testcontainer", "http");

        var message = await client.GetStringAsync("/aspire.html");

        Assert.Equal($"{DefaultMessage}\n", message);

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var containers = await kubernetes.ListAsync<Container>();

        var container = Assert.Single(containers);
        Assert.Equal(tempContextPath, container!.Spec!.Build!.Context);
        Assert.Equal(tempDockerfilePath, container!.Spec!.Build!.Dockerfile);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task AddDockerfileLaunchesContainerSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.AddDockerfile("testcontainer", tempContextPath, tempDockerfilePath)
               .WithHttpEndpoint(targetPort: 80);

        using var app = builder.Build();
        await app.StartAsync();

        await WaitForResourceAsync(app, "testcontainer", "Running");

        using var client = app.CreateHttpClient("testcontainer", "http");
        var message = await client.GetStringAsync("/aspire.html");

        Assert.Equal($"{DefaultMessage}\n", message);

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var containers = await kubernetes.ListAsync<Container>();

        var container = Assert.Single<Container>(containers);
        Assert.Equal(tempContextPath, container!.Spec!.Build!.Context);
        Assert.Equal(tempDockerfilePath, container!.Spec!.Build!.Dockerfile);

        await app.StopAsync();
    }

    [Fact]
    public async Task WithDockerfileResultsInBuildAttributeBeingAddedToManifest()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var manifestOutputPath = Path.Combine(tempContextPath, "aspire-manifest.json");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest", "--output-path", manifestOutputPath],
        });
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        builder.Configuration["Parameters:message"] = "hello";
        var parameter = builder.AddParameter("message");

        var container = builder.AddContainer("testcontainer", "testimage")
                               .WithHttpEndpoint(targetPort: 80)
                               .WithDockerfile(tempContextPath, tempDockerfilePath, "runner")
                               .WithBuildArg("MESSAGE", parameter)
                               .WithBuildArg("stringParam", "a string")
                               .WithBuildArg("intParam", 42);

        var manifest = await ManifestUtils.GetManifest(container.Resource, manifestDirectory: tempContextPath);
        var expectedManifest = $$$$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "stage": "runner",
                "args": {
                  "MESSAGE": "{message.value}",
                  "stringParam": "a string",
                  "intParam": "42"
                }
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task AddDockerfileResultsInBuildAttributeBeingAddedToManifest()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var manifestOutputPath = Path.Combine(tempContextPath, "aspire-manifest.json");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest", "--output-path", manifestOutputPath],
        });
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        builder.Configuration["Parameters:message"] = "hello";
        var parameter = builder.AddParameter("message");

        var container = builder.AddDockerfile("testcontainer", tempContextPath, tempDockerfilePath, "runner")
                               .WithHttpEndpoint(targetPort: 80)
                               .WithBuildArg("MESSAGE", parameter)
                               .WithBuildArg("stringParam", "a string")
                               .WithBuildArg("intParam", 42);

        var manifest = await ManifestUtils.GetManifest(container.Resource, manifestDirectory: tempContextPath);
        var expectedManifest = $$$$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "stage": "runner",
                "args": {
                  "MESSAGE": "{message.value}",
                  "stringParam": "a string",
                  "intParam": "42"
                }
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task WithDockerfileWithBuildSecretResultsInManifestReferencingSecretParameter()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var manifestOutputPath = Path.Combine(tempContextPath, "aspire-manifest.json");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest", "--output-path", manifestOutputPath],
        });
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        builder.Configuration["Parameters:secret"] = "open sesame";
        var parameter = builder.AddParameter("secret", secret: true);

        var container = builder.AddContainer("testcontainer", "testimage")
                               .WithHttpEndpoint(targetPort: 80)
                               .WithDockerfile(tempContextPath, tempDockerfilePath)
                               .WithBuildSecret("SECRET", parameter);

        var manifest = await ManifestUtils.GetManifest(container.Resource, manifestDirectory: tempContextPath);
        var expectedManifest = $$$$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "secrets": {
                  "SECRET": {
                    "type": "env",
                    "value": "{secret.value}"
                  }
                }
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task AddDockerfileWithBuildSecretResultsInManifestReferencingSecretParameter()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var manifestOutputPath = Path.Combine(tempContextPath, "aspire-manifest.json");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest", "--output-path", manifestOutputPath],
        });
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        builder.Configuration["Parameters:secret"] = "open sesame";
        var parameter = builder.AddParameter("secret", secret: true);

        var container = builder.AddDockerfile("testcontainer", tempContextPath, tempDockerfilePath)
                               .WithHttpEndpoint(targetPort: 80)
                               .WithBuildSecret("SECRET", parameter);

        var manifest = await ManifestUtils.GetManifest(container.Resource, manifestDirectory: tempContextPath);
        var expectedManifest = $$$$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile",
                "secrets": {
                  "SECRET": {
                    "type": "env",
                    "value": "{secret.value}"
                  }
                }
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 80
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task WithDockerfileWithParameterLaunchesContainerSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.Configuration["Parameters:message"] = "hello";
        var parameter = builder.AddParameter("message");

        builder.AddContainer("testcontainer", "testimage")
               .WithHttpEndpoint(targetPort: 80)
               .WithDockerfile(tempContextPath, tempDockerfilePath)
               .WithBuildArg("MESSAGE", parameter)
               .WithBuildArg("stringParam", "a string")
               .WithBuildArg("intParam", 42)
               .WithBuildArg("boolParamTrue", true)
               .WithBuildArg("boolParamFalse", false);

        using var app = builder.Build();
        await app.StartAsync();

        await WaitForResourceAsync(app, "testcontainer", "Running");

        using var client = app.CreateHttpClient("testcontainer", "http");

        var message = await client.GetStringAsync("/aspire.html");

        Assert.Equal($"hello\n", message);

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var containers = await kubernetes.ListAsync<Container>();

        var container = Assert.Single<Container>(containers);
        Assert.Equal(tempContextPath, container!.Spec!.Build!.Context);
        Assert.Equal(tempDockerfilePath, container!.Spec!.Build!.Dockerfile);
        Assert.Null(container!.Spec!.Build!.Stage);
        Assert.Collection(
            container!.Spec!.Build!.Args!,
            arg =>
            {
                Assert.Equal("MESSAGE", arg.Name);
                Assert.Equal("hello", arg.Value);
            },
            arg =>
            {
                Assert.Equal("stringParam", arg.Name);
                Assert.Equal("a string", arg.Value);
            },
            arg =>
            {
                Assert.Equal("intParam", arg.Name);
                Assert.Equal("42", arg.Value);
            },
            arg =>
            {
                Assert.Equal("boolParamTrue", arg.Name);
                Assert.Equal("true", arg.Value);
            },
            arg =>
            {
                Assert.Equal("boolParamFalse", arg.Name);
                Assert.Equal("false", arg.Value);
            }
            );

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task AddDockerfileWithParameterLaunchesContainerSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.Configuration["Parameters:message"] = "hello";
        var parameter = builder.AddParameter("message");

        builder.AddDockerfile("testcontainer", tempContextPath, tempDockerfilePath)
               .WithHttpEndpoint(targetPort: 80)
               .WithBuildArg("MESSAGE", parameter)
               .WithBuildArg("stringParam", "a string")
               .WithBuildArg("intParam", 42)
               .WithBuildArg("boolParamTrue", true)
               .WithBuildArg("boolParamFalse", false);

        using var app = builder.Build();
        await app.StartAsync();

        await WaitForResourceAsync(app, "testcontainer", "Running");

        using var client = app.CreateHttpClient("testcontainer", "http");

        var message = await client.GetStringAsync("/aspire.html");

        Assert.Equal($"hello\n", message);

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var containers = await kubernetes.ListAsync<Container>();

        var container = Assert.Single<Container>(containers);
        Assert.Equal(tempContextPath, container!.Spec!.Build!.Context);
        Assert.Equal(tempDockerfilePath, container!.Spec!.Build!.Dockerfile);
        Assert.Null(container!.Spec!.Build!.Stage);
        Assert.Collection(
            container!.Spec!.Build!.Args!,
            arg =>
            {
                Assert.Equal("MESSAGE", arg.Name);
                Assert.Equal("hello", arg.Value);
            },
            arg =>
            {
                Assert.Equal("stringParam", arg.Name);
                Assert.Equal("a string", arg.Value);
            },
            arg =>
            {
                Assert.Equal("intParam", arg.Name);
                Assert.Equal("42", arg.Value);
            },
            arg =>
            {
                Assert.Equal("boolParamTrue", arg.Name);
                Assert.Equal("true", arg.Value);
            },
            arg =>
            {
                Assert.Equal("boolParamFalse", arg.Name);
                Assert.Equal("false", arg.Value);
            }
            );

        await app.StopAsync();
    }

    [Fact]
    public void WithDockerfileWithEmptyContextPathThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            builder.AddContainer("mycontainer", "myimage")
                   .WithDockerfile(string.Empty);
        });

        Assert.Equal("contextPath", ex.ParamName);
    }

    [Fact]
    public void AddDockerfileWithEmptyContextPathThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            builder.AddDockerfile("mycontainer", string.Empty)
                   .WithDockerfile(string.Empty);
        });

        Assert.Equal("contextPath", ex.ParamName);
    }

    [Fact]
    public void WithBuildArgsBeforeWithDockerfileThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        var container = builder.AddContainer("mycontainer", "myimage");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            container.WithBuildArg("MESSAGE", "hello");
        });

        Assert.Equal(
            "The resource does not have a Dockerfile build annotation. Call WithDockerfile before calling WithBuildArg.",
            ex.Message
            );
    }

    [Fact]
    public async Task WithDockerfileWithValidContextPathValidDockerfileWithImplicitDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task AddDockerfileWithValidContextPathValidDockerfileWithImplicitDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddDockerfile("mycontainer", tempContextPath);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task WithDockerfileWithValidContextPathValidDockerfileWithExplicitDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath, "Dockerfile");

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task AddDockerfileWithValidContextPathValidDockerfileWithExplicitDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddDockerfile("mycontainer", tempContextPath, "Dockerfile");

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task WithDockerfileWithValidContextPathValidDockerfileWithExplicitNonDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync("Otherdockerfile");
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath, "Otherdockerfile");

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task AddDockerfileWithValidContextPathValidDockerfileWithExplicitNonDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync("Otherdockerfile");
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddDockerfile("mycontainer", tempContextPath, "Otherdockerfile");

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task WithDockerfileWithValidContextPathValidDockerfileWithExplicitAbsoluteDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath, tempDockerfilePath);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    [Fact]
    public async Task AddDockerfileWithValidContextPathValidDockerfileWithExplicitAbsoluteDefaultNameSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        var container = builder.AddDockerfile("mycontainer", tempContextPath, tempDockerfilePath);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal(tempDockerfilePath, annotation.DockerfilePath);
    }

    private static async Task WaitForResourceAsync(DistributedApplication app, string resourceName, string resourceState, TimeSpan? timeout = null)
    {
        await app.ResourceNotifications.WaitForResourceAsync(resourceName, resourceState).WaitAsync(timeout ?? TimeSpan.FromMinutes(3));
    }

    private const string DefaultMessage = "aspire!";

    [Fact]
    public async Task WithDockerfileFactorySyncFactoryCreatesAnnotationWithFactory()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(createDockerfile: false);
        var tempContextPath = tempDockerfileContext.ContextPath;

        var dockerfileContent = "FROM alpine:latest\nRUN echo 'Hello from factory'";
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfileFactory(tempContextPath, context => dockerfileContent);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.NotNull(annotation.DockerfileFactory);

        var stepsAnnotation = Assert.Single(container.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = container.Resource
        };
        var steps = (await stepsAnnotation.CreateStepsAsync(factoryContext)).ToList();
        Assert.Equal(2, steps.Count);

        var buildStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.BuildCompute));
        Assert.Equal("build-mycontainer", buildStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Build, buildStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.BuildPrereq, buildStep.DependsOnSteps);

        var pushStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
        Assert.Equal("push-mycontainer", pushStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Push, pushStep.RequiredBySteps);

        // Verify the factory produces the expected content
        var context = new DockerfileFactoryContext
        {
            Services = builder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };
        var generatedContent = await annotation.DockerfileFactory(context);

        await Verify(generatedContent);
    }

    [Fact]
    public async Task WithDockerfileFactoryAsyncFactoryCreatesAnnotationWithFactory()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(createDockerfile: false);
        var tempContextPath = tempDockerfileContext.ContextPath;

        var dockerfileContent = "FROM alpine:latest\nRUN echo 'Hello from async factory'";
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfileFactory(tempContextPath, async context =>
                               {
                                   await Task.Delay(1, context.CancellationToken);
                                   return dockerfileContent;
                               });

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.NotNull(annotation.DockerfileFactory);

        // Verify the factory produces the expected content
        var context = new DockerfileFactoryContext
        {
            Services = builder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };
        var generatedContent = await annotation.DockerfileFactory(context);

        await Verify(generatedContent);
    }

    [Fact]
    public async Task WithDockerfileFactoryGeneratesFileAtBuildTime()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(createDockerfile: false);

        var tempContextPath = tempDockerfileContext.ContextPath;
        var manifestOutputPath = Path.Combine(tempContextPath, "aspire-manifest.json");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest", "--output-path", manifestOutputPath],
        });
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        var dockerfileContent = "FROM alpine:latest\nRUN echo 'Generated at build time'";
        var container = builder.AddContainer("testcontainer", "testimage")
                               .WithHttpEndpoint(targetPort: 80)
                               .WithDockerfileFactory(tempContextPath, context => dockerfileContent);

        var manifest = await ManifestUtils.GetManifest(container.Resource, manifestDirectory: tempContextPath);

        await Verify(manifest.ToString());
    }

    [Fact]
    public async Task WithDockerfileFactoryWithStageAndBuildArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(createDockerfile: false);
        var tempContextPath = tempDockerfileContext.ContextPath;

        var dockerfileContent = "FROM alpine:latest AS builder\nFROM alpine:latest AS runner";
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfileFactory(tempContextPath, context => dockerfileContent, "runner")
                               .WithBuildArg("VERSION", "1.0");

        var annotation = Assert.Single(container.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(tempContextPath, annotation.ContextPath);
        Assert.Equal("runner", annotation.Stage);
        Assert.NotNull(annotation.DockerfileFactory);
        Assert.Single(annotation.BuildArguments);
        Assert.Equal("1.0", annotation.BuildArguments["VERSION"]);
    }

    [Fact]
    public async Task ManifestPublishingWritesDockerfileToResourceSpecificPath()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var manifestPath = Path.Combine(tempDir.FullName, "manifest.json");
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = ["--publisher", "manifest", "--output-path", manifestPath],
            });
            builder.Services.AddLogging(b => b.AddXunit(testOutputHelper));

            var dockerfileContent = "FROM alpine:latest\nRUN echo 'Generated for manifest'";
            var container = builder.AddContainer("testcontainer", "testimage")
                                   .WithDockerfileFactory(tempDir.FullName, context => dockerfileContent);

            var app = builder.Build();
            await app.RunAsync();

            // Verify Dockerfile was written to resource-specific path
            var dockerfilePath = Path.Combine(tempDir.FullName, "testcontainer.Dockerfile");
            Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");
            var actualContent = await File.ReadAllTextAsync(dockerfilePath);

            // Verify manifest references the Dockerfile
            var manifestContent = await File.ReadAllTextAsync(manifestPath);

            await Verify(actualContent)
                  .AppendContentAsFile(manifestContent, "json");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithDockerfile_AutomaticallyGeneratesBuildStep_WithCorrectDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath = tempDockerfileContext.ContextPath;
        var tempDockerfilePath = tempDockerfileContext.DockerfilePath;
        builder.AddContainer("test-container", "test-image")
               .WithDockerfile(tempContextPath, tempDockerfilePath);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources = appModel.GetContainerResources();

        var resource = Assert.Single(containerResources);

        // Verify the container has a PipelineStepAnnotation
        var pipelineStepAnnotation = Assert.Single(resource.Annotations.OfType<PipelineStepAnnotation>());

        // Create a factory context for testing the annotation
        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!, // Not needed for this test
            Resource = resource
        };

        var steps = (await pipelineStepAnnotation.CreateStepsAsync(factoryContext)).ToList();
        Assert.Equal(2, steps.Count);

        var buildStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.BuildCompute));
        Assert.Equal("build-test-container", buildStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Build, buildStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.BuildPrereq, buildStep.DependsOnSteps);

        var pushStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
        Assert.Equal("push-test-container", pushStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Push, pushStep.RequiredBySteps);
    }

    [Fact]
    public async Task WithDockerfile_CalledMultipleTimes_OverwritesPreviousBuildStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        using var tempDockerfileContext1 = await DockerfileUtils.CreateTemporaryDockerfileAsync();
        var tempContextPath1 = tempDockerfileContext1.ContextPath;
        var tempDockerfilePath1 = tempDockerfileContext1.DockerfilePath;
        using var tempDockerfileContext2 = await DockerfileUtils.CreateTemporaryDockerfileAsync();

        var tempContextPath2 = tempDockerfileContext2.ContextPath;

        var tempDockerfilePath2 = tempDockerfileContext2.DockerfilePath;
        var containerBuilder = builder.AddContainer("test-container", "test-image")
                                     .WithDockerfile(tempContextPath1, tempDockerfilePath1)
                                     .WithDockerfile(tempContextPath1, tempDockerfilePath1); // Call twice to start

        using var app1 = builder.Build();
        var appModel1 = app1.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResources1 = appModel1.GetContainerResources();
        var resource1 = Assert.Single(containerResources1);

        // Get the first pipeline step annotation
        var pipelineStepAnnotation1 = Assert.Single(resource1.Annotations.OfType<PipelineStepAnnotation>());

        // Both should create the same build step name
        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!, // Not needed for this test
            Resource = resource1
        };

        var steps = (await pipelineStepAnnotation1.CreateStepsAsync(factoryContext)).ToList();
        Assert.Equal(2, steps.Count);

        var buildStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.BuildCompute));
        Assert.Equal("build-test-container", buildStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Build, buildStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.BuildPrereq, buildStep.DependsOnSteps);

        var pushStep = steps.Single(s => s.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
        Assert.Equal("push-test-container", pushStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Push, pushStep.RequiredBySteps);
    }
}
