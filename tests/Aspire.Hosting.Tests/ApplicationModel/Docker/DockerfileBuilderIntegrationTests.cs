// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using System.Text;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuilderIntegrationTests
{
    [Fact]
    public async Task ExampleFromProblemStatement_ProducesCorrectDockerfile()
    {
        // Arrange - this is the exact example from the problem statement
        var dockerfileBuilder = new DockerfileBuilder();
        var baseStage = dockerfileBuilder.From("node:20-bullseye", "builder");
        baseStage.WorkDir("/");
        baseStage.Run("apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*");
        baseStage.Copy("package*.json", "./");
        baseStage.Run("npm ci --silent");
        baseStage.Env("NODE_ENV", "production");

        var output = dockerfileBuilder.From("caddy:2.7.4-alpine");
        output.CopyFrom("builder", "/app/dist", "/srv");
        output.Copy("caddy.json", "/etc/caddy/caddy.json");
        output.Expose(80);
        output.Cmd(["cmd", "run", "--config", "/etc/caddy/caddy.json"]);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await dockerfileBuilder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM node:20-bullseye AS builder
            WORKDIR /
            RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
            COPY package*.json ./
            RUN npm ci --silent
            ENV NODE_ENV=production

            FROM caddy:2.7.4-alpine
            COPY --from=builder /app/dist /srv
            COPY caddy.json /etc/caddy/caddy.json
            EXPOSE 80
            CMD ["cmd","run","--config","/etc/caddy/caddy.json"]

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void AddRemoveStatements_WorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("nginx");

        // Act - Add multiple statements
        stage.WorkDir("/app");
        stage.Run("echo 'test'");
        stage.Env("TEST", "value");

        // Assert - can access and modify the statements collection
        Assert.Equal(4, stage.Statements.Count); // FROM + WORKDIR + RUN + ENV

        // Act - Remove the ENV statement
        stage.Statements.RemoveAt(3);

        // Assert
        Assert.Equal(3, stage.Statements.Count);
    }

    [Fact]
    public async Task MultipleStagesWithComplexCommands_ProducesCorrectOutput()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        
        // Stage 1: Build stage
        var buildStage = builder.From("golang:1.20", "build");
        buildStage.WorkDir("/src");
        buildStage.Copy("go.mod", "./");
        buildStage.Copy("go.sum", "./");
        buildStage.Run("go mod download");
        buildStage.Copy(".", ".");
        buildStage.Run("CGO_ENABLED=0 GOOS=linux go build -o /app/server ./cmd/server");

        // Stage 2: Runtime stage
        var runtimeStage = builder.From("alpine:latest");
        runtimeStage.Run("apk --no-cache add ca-certificates");
        runtimeStage.WorkDir("/root/");
        runtimeStage.CopyFrom("build", "/app/server", "./");
        runtimeStage.Env("PORT", "8080");
        runtimeStage.Expose(8080);
        runtimeStage.Cmd(["./server"]);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM golang:1.20 AS build
            WORKDIR /src
            COPY go.mod ./
            COPY go.sum ./
            RUN go mod download
            COPY . .
            RUN CGO_ENABLED=0 GOOS=linux go build -o /app/server ./cmd/server

            FROM alpine:latest
            RUN apk --no-cache add ca-certificates
            WORKDIR /root/
            COPY --from=build /app/server ./
            ENV PORT=8080
            EXPOSE 8080
            CMD ["./server"]

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void FluentAPI_SupportsMethodChaining()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act - demonstrate fluent API works correctly
        var stage = builder.From("node:18")
            .WorkDir("/app")
            .Copy("package.json", "./")
            .Run("npm install")
            .Copy(".", ".")
            .Env("NODE_ENV", "production")
            .Expose(3000)
            .Cmd(["npm", "start"]);

        // Assert
        Assert.Equal(8, stage.Statements.Count); // FROM + WORKDIR + COPY + RUN + COPY + ENV + EXPOSE + CMD
        Assert.Null(stage.StageName); // This stage has no name
    }

    [Fact]
    public void DockerfileBuilder_WithMultipleStages_ManagesStagesCorrectly()
    {
        // Arrange & Act
        var dockerfileBuilder = new DockerfileBuilder();

        // Assert
        Assert.Empty(dockerfileBuilder.Stages);
        
        // Act - add stages
        var stage1 = dockerfileBuilder.From("alpine");
        var stage2 = dockerfileBuilder.From("ubuntu");
        
        // Assert
        Assert.Equal(2, dockerfileBuilder.Stages.Count);
        Assert.Same(stage1, dockerfileBuilder.Stages[0]);
        Assert.Same(stage2, dockerfileBuilder.Stages[1]);
    }
}