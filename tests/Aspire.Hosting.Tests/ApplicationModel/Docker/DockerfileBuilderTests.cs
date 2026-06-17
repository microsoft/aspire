// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using System.Text;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuilderTests
{
    [Fact]
    public void DockerfileBuilder_Constructor_InitializesEmpty()
    {
        // Arrange & Act
        var builder = new DockerfileBuilder();

        // Assert
        Assert.Empty(builder.Stages);
    }

    [Fact]
    public void From_WithImageOnly_CreatesStage()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node");

        // Assert
        Assert.NotNull(stage);
        Assert.Null(stage.StageName);
        Assert.Single(builder.Stages);
        Assert.Single(stage.Statements);
    }

    [Fact]
    public void From_WithImageAndTag_CreatesStage()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node:20-bullseye");

        // Assert
        Assert.NotNull(stage);
        Assert.Null(stage.StageName);
        Assert.Single(builder.Stages);
        Assert.Single(stage.Statements);
    }

    [Fact]
    public void From_WithImageAndStageName_CreatesNamedStage()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node:20-bullseye", "builder");

        // Assert
        Assert.NotNull(stage);
        Assert.Equal("builder", stage.StageName);
        Assert.Single(builder.Stages);
        Assert.Single(stage.Statements);
    }

    [Fact]
    public void From_WithNullOrEmptyImage_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.From(""));
        Assert.Throws<ArgumentNullException>(() => builder.From(null!));
    }

    [Fact]
    public void From_WithNullOrEmptyStageName_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.From("node", ""));
        Assert.Throws<ArgumentNullException>(() => builder.From("node", null!));
    }

    [Fact]
    public void MultipleFromStatements_CreateMultipleStages()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage1 = builder.From("node:20-bullseye", "builder");
        var stage2 = builder.From("caddy:2.7.4-alpine");

        // Assert
        Assert.Equal(2, builder.Stages.Count);
        Assert.Equal("builder", stage1.StageName);
        Assert.Null(stage2.StageName);
        Assert.Same(stage1, builder.Stages[0]);
        Assert.Same(stage2, builder.Stages[1]);
    }

    [Fact]
    public async Task WriteAsync_WithSingleStage_WritesCorrectContent()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node:20-bullseye");
        stage.WorkDir("/app");
        stage.Run("npm install");
        stage.Expose(3000);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM node:20-bullseye
            WORKDIR /app
            RUN npm install
            EXPOSE 3000

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task WriteAsync_WithMultipleStages_WritesCorrectContent()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        
        var stage1 = builder.From("node:20-bullseye", "builder");
        stage1.WorkDir("/app");
        stage1.Copy("package*.json", "./");
        stage1.Run("npm ci --silent");
        stage1.Env("NODE_ENV", "production");

        var stage2 = builder.From("caddy:2.7.4-alpine");
        stage2.CopyFrom("builder", "/app/dist", "/srv");
        stage2.Copy("caddy.json", "/etc/caddy/caddy.json");
        stage2.Expose(80);
        stage2.Cmd(["cmd", "run", "--config", "/etc/caddy/caddy.json"]);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM node:20-bullseye AS builder
            WORKDIR /app
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
    public async Task WriteAsync_WithNullStream_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        builder.From("node");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => builder.WriteAsync(null!));
    }

    [Fact]
    public async Task WriteAsync_WithEmptyBuilder_WritesNothing()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        Assert.Equal(0, stream.Length);
    }
}