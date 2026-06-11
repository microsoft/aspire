// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using System.Collections.ObjectModel;
using System.Text;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuilderEdgeCasesTests
{
    [Fact]
    public async Task EmptyDockerfile_WritesNothing()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task SingleStageWithOnlyFrom_WritesOnlyFromStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        builder.From("ubuntu");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM ubuntu\n", content);
    }

    [Fact]
    public void EnvStatement_WithEmptyStringValue_AllowedAndWorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");

        // Act & Assert - Should not throw
        stage.Env("EMPTY_VAR", "");
        Assert.Equal(2, stage.Statements.Count);
    }

    [Fact]
    public async Task EnvStatement_WithEmptyStringValue_WritesCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        stage.Env("EMPTY_VAR", "");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM alpine
            ENV EMPTY_VAR=

            """.ReplaceLineEndings("\n");
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task CmdStatement_WithComplexCommand_SerializesCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");
        stage.Cmd(["node", "-e", "console.log('Hello, World!')", "--port", "3000"]);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM node
            CMD ["node","-e","console.log('Hello, World!')","--port","3000"]

            """.ReplaceLineEndings("\n");
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task StageOrdering_PreservesSequence()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        
        // Add stages in specific order
        _ = builder.From("alpine:3.16", "first");
        _ = builder.From("node:18", "second");
        _ = builder.From("nginx", "third");

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            FROM alpine:3.16 AS first

            FROM node:18 AS second

            FROM nginx AS third

            """.ReplaceLineEndings("\n");
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void StatementCollection_IsModifiable()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("ubuntu");
        
        // Act
        stage.Run("apt-get update");
        stage.Run("apt-get install -y curl");
        
        // Verify we can access and modify the collection directly
        Assert.Equal(3, stage.Statements.Count); // FROM + 2 RUN
        
        // Remove the first RUN statement (index 1, since FROM is at index 0)
        stage.Statements.RemoveAt(1);
        
        // Assert
        Assert.Equal(2, stage.Statements.Count); // FROM + 1 RUN
    }

    [Fact]
    public async Task RunStatement_WithMultiLineCommand_PreservesFormatting()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("ubuntu");
        var multiLineCommand = """
            apt-get update && \
            apt-get install -y \
                curl \
                wget \
                vim && \
            rm -rf /var/lib/apt/lists/*
            """.ReplaceLineEndings("\n");
        stage.Run(multiLineCommand);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = $$"""
            FROM ubuntu
            RUN {{multiLineCommand}}

            """.ReplaceLineEndings("\n");
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void ReadOnlyStagesCollection_PreventsDirectModification()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        builder.From("alpine");

        // Act & Assert
        Assert.IsType<ReadOnlyCollection<DockerfileStage>>(builder.Stages);
        Assert.Single(builder.Stages);
    }

    [Fact]
    public async Task WriteAsync_WithLargeDockerfile_HandlesCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("ubuntu:20.04");
        
        // Add many statements to test performance and correctness
        for (var i = 0; i < 100; i++)
        {
            stage.Run($"echo 'Statement {i}'");
        }
        
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        Assert.Equal(101, lines.Length); // FROM + 100 RUN statements
        Assert.StartsWith("FROM ubuntu:20.04", lines[0]);
        Assert.StartsWith("RUN echo 'Statement 0'", lines[1]);
        Assert.StartsWith("RUN echo 'Statement 99'", lines[100]);
    }
}