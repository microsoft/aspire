// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class WithDockerfileBuilderTests
{
    [Fact]
    public void WithDockerfileBuilder_WithCallback_CreatesCallbackAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        // Act
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.From("alpine:latest");
        });

        // Assert
        var callbackAnnotation = container.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();
        Assert.NotNull(callbackAnnotation);
        Assert.Single(callbackAnnotation.Callbacks);
    }

    [Fact]
    public void WithDockerfileBuilder_MultipleCallbacks_AppendsToAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        // Act
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.From("alpine:latest");
        });

        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.Stages[0].WorkDir("/app");
        });

        // Assert
        var callbackAnnotation = container.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();
        Assert.NotNull(callbackAnnotation);
        Assert.Equal(2, callbackAnnotation.Callbacks.Count);
    }

    [Fact]
    public void WithDockerfileBuilder_CreatesDockerfileBuildAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        // Act
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.From("alpine:latest");
        });

        // Assert
        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);
        Assert.NotNull(buildAnnotation.DockerfileFactory);
    }

    [Fact]
    public async Task WithDockerfileBuilder_GeneratesDockerfileFromCallback()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.From("alpine:latest")
                .WorkDir("/app")
                .Run("apk add curl")
                .Copy(".", ".")
                .Cmd(["./myapp"]);
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory(factoryContext);

        // Assert
        Assert.Contains("FROM alpine:latest", dockerfile);
        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("RUN apk add curl", dockerfile);
        Assert.Contains("COPY . .", dockerfile);
        Assert.Contains("CMD [\"./myapp\"]", dockerfile);
    }

    [Fact]
    public async Task WithDockerfileBuilder_MultipleCallbacks_BuildsComposedDockerfile()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        // First callback - base setup
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.From("node:18")
                .WorkDir("/app");
        });

        // Second callback - add dependencies
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.Stages[0]
                .Copy("package*.json", "./")
                .Run("npm ci");
        });

        // Third callback - add application
        container.WithDockerfileBuilder("context", context =>
        {
            context.Builder.Stages[0]
                .Copy(".", ".")
                .Cmd(["node", "index.js"]);
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory!(factoryContext);

        // Assert
        Assert.Contains("FROM node:18", dockerfile);
        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY package*.json ./", dockerfile);
        Assert.Contains("RUN npm ci", dockerfile);
        Assert.Contains("COPY . .", dockerfile);
        Assert.Contains("CMD [\"node\",\"index.js\"]", dockerfile);
    }

    [Fact]
    public async Task WithDockerfileBuilder_AsyncCallback_Works()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        container.WithDockerfileBuilder("context", async context =>
        {
            await Task.Delay(10); // Simulate async work
            context.Builder.From("alpine:latest")
                .Run("echo test");
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory!(factoryContext);

        // Assert
        Assert.Contains("FROM alpine:latest", dockerfile);
        Assert.Contains("RUN echo test", dockerfile);
    }

    [Fact]
    public async Task WithDockerfileBuilder_ContextProvidesServices()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<string>("test-config-value");
        
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        container.WithDockerfileBuilder("context", context =>
        {
            var config = context.Services.GetService<string>();
            context.Builder.From("alpine:latest")
                .Env("CONFIG", config ?? "default");
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory!(factoryContext);

        // Assert
        Assert.Contains("ENV CONFIG=test-config-value", dockerfile);
    }

    [Fact]
    public async Task WithDockerfileBuilder_MultiStageDockerfile()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var container = appBuilder.AddContainer("mycontainer", "myimage");

        container.WithDockerfileBuilder("context", context =>
        {
            // Builder stage
            var builder = context.Builder.From("node:18", "builder");
            builder.WorkDir("/build")
                .Copy("package*.json", "./")
                .Run("npm ci")
                .Copy(".", ".")
                .Run("npm run build");

            // Runtime stage
            var runtime = context.Builder.From("node:18-alpine");
            runtime.WorkDir("/app")
                .CopyFrom("builder", "/build/dist", "./")
                .Cmd(["node", "index.js"]);
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory!(factoryContext);

        // Assert
        Assert.Contains("FROM node:18 AS builder", dockerfile);
        Assert.Contains("FROM node:18-alpine", dockerfile);
        Assert.Contains("COPY --from=builder /build/dist ./", dockerfile);
    }

    [Fact]
    public void AddDockerfileBuilder_WithAsyncCallback_CreatesContainerResource()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", async context =>
        {
            await Task.Delay(10);
            context.Builder.From("alpine:latest")
                .WorkDir("/app");
        });

        // Assert
        Assert.NotNull(container);
        Assert.Equal("mycontainer", container.Resource.Name);
        var callbackAnnotation = container.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();
        Assert.NotNull(callbackAnnotation);
        Assert.Single(callbackAnnotation.Callbacks);
    }

    [Fact]
    public void AddDockerfileBuilder_WithSyncCallback_CreatesContainerResource()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", context =>
        {
            context.Builder.From("alpine:latest")
                .WorkDir("/app");
        });

        // Assert
        Assert.NotNull(container);
        Assert.Equal("mycontainer", container.Resource.Name);
        var callbackAnnotation = container.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();
        Assert.NotNull(callbackAnnotation);
        Assert.Single(callbackAnnotation.Callbacks);
    }

    [Fact]
    public async Task AddDockerfileBuilder_WithAsyncCallback_GeneratesDockerfile()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", async context =>
        {
            await Task.Delay(10);
            context.Builder.From("node:18")
                .WorkDir("/app")
                .Copy("package*.json", "./")
                .Run("npm ci")
                .Copy(".", ".")
                .Cmd(["node", "index.js"]);
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory(factoryContext);

        // Assert
        Assert.Contains("FROM node:18", dockerfile);
        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY package*.json ./", dockerfile);
        Assert.Contains("RUN npm ci", dockerfile);
        Assert.Contains("COPY . .", dockerfile);
        Assert.Contains("CMD [\"node\",\"index.js\"]", dockerfile);
    }

    [Fact]
    public async Task AddDockerfileBuilder_WithSyncCallback_GeneratesDockerfile()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", context =>
        {
            context.Builder.From("alpine:latest")
                .Run("apk add curl")
                .WorkDir("/workspace");
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory(factoryContext);

        // Assert
        Assert.Contains("FROM alpine:latest", dockerfile);
        Assert.Contains("RUN apk add curl", dockerfile);
        Assert.Contains("WORKDIR /workspace", dockerfile);
    }

    [Fact]
    public void AddDockerfileBuilder_WithCallback_CreatesDockerfileBuildAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", context =>
        {
            context.Builder.From("alpine:latest");
        });

        // Act
        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();

        // Assert
        Assert.NotNull(buildAnnotation);
        Assert.NotNull(buildAnnotation.ContextPath);
        Assert.NotNull(buildAnnotation.DockerfilePath);
        Assert.NotNull(buildAnnotation.DockerfileFactory);
    }

    [Fact]
    public async Task AddDockerfileBuilder_WithMultiStage_GeneratesCorrectDockerfile()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", context =>
        {
            // Build stage
            var buildStage = context.Builder.From("golang:1.20", "build");
            buildStage.WorkDir("/src")
                .Copy("go.mod", "./")
                .Copy("go.sum", "./")
                .Run("go mod download")
                .Copy(".", ".")
                .Run("go build -o /app/server");

            // Runtime stage
            var runtimeStage = context.Builder.From("alpine:latest");
            runtimeStage.Run("apk add ca-certificates")
                .WorkDir("/root/")
                .CopyFrom("build", "/app/server", "./")
                .Cmd(["./server"]);
        });

        var buildAnnotation = container.Resource.Annotations.OfType<DockerfileBuildAnnotation>().LastOrDefault();
        Assert.NotNull(buildAnnotation);

        var factoryContext = new DockerfileFactoryContext
        {
            Services = appBuilder.Services.BuildServiceProvider(),
            Resource = container.Resource,
            CancellationToken = CancellationToken.None
        };

        // Act
        var dockerfile = await buildAnnotation.DockerfileFactory!(factoryContext);

        // Assert
        Assert.Contains("FROM golang:1.20 AS build", dockerfile);
        Assert.Contains("FROM alpine:latest", dockerfile);
        Assert.Contains("COPY --from=build /app/server ./", dockerfile);
        Assert.Contains("CMD [\"./server\"]", dockerfile);
    }

    [Fact]
    public void AddDockerfileBuilder_WithCallback_CanChainWithOtherMethods()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var container = appBuilder.AddDockerfileBuilder("mycontainer", "context", context =>
        {
            context.Builder.From("alpine:latest");
        })
        .WithEnvironment("ENV_VAR", "value")
        .WithEndpoint(8080, 80, "http");

        // Assert
        Assert.NotNull(container);
        var envAnnotation = container.Resource.Annotations.OfType<EnvironmentAnnotation>().FirstOrDefault();
        Assert.NotNull(envAnnotation);
        var endpointAnnotation = container.Resource.Annotations.OfType<EndpointAnnotation>().FirstOrDefault();
        Assert.NotNull(endpointAnnotation);
    }
}
