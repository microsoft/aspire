// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using System.Text;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileStageTests
{
    [Fact]
    public void WorkDir_WithValidPath_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.WorkDir("/app");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + WORKDIR
    }

    [Fact]
    public void WorkDir_WithNullOrEmptyPath_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.WorkDir(""));
        Assert.Throws<ArgumentNullException>(() => stage.WorkDir(null!));
    }

    [Fact]
    public void Run_WithValidCommand_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.Run("npm install");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + RUN
    }

    [Fact]
    public void Run_WithNullOrEmptyCommand_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.Run(""));
        Assert.Throws<ArgumentNullException>(() => stage.Run(null!));
    }

    [Fact]
    public void Copy_WithValidParameters_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.Copy("src/", "./");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + COPY
    }

    [Fact]
    public void Copy_WithNullOrEmptyParameters_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.Copy("", "./"));
        Assert.Throws<ArgumentNullException>(() => stage.Copy(null!, "./"));
        Assert.Throws<ArgumentException>(() => stage.Copy("src/", ""));
        Assert.Throws<ArgumentNullException>(() => stage.Copy("src/", null!));
    }

    [Fact]
    public void CopyFrom_WithValidParameters_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("nginx");

        // Act
        var result = stage.CopyFrom("builder", "/app/dist", "/usr/share/nginx/html");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + COPY --from
    }

    [Fact]
    public void CopyFrom_WithNullOrEmptyParameters_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("nginx");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.CopyFrom("", "/app", "/dest"));
        Assert.Throws<ArgumentNullException>(() => stage.CopyFrom(null!, "/app", "/dest"));
        Assert.Throws<ArgumentException>(() => stage.CopyFrom("builder", "", "/dest"));
        Assert.Throws<ArgumentNullException>(() => stage.CopyFrom("builder", null!, "/dest"));
        Assert.Throws<ArgumentException>(() => stage.CopyFrom("builder", "/app", ""));
        Assert.Throws<ArgumentNullException>(() => stage.CopyFrom("builder", "/app", null!));
    }

    [Fact]
    public void Env_WithValidParameters_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.Env("NODE_ENV", "production");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + ENV
    }

    [Fact]
    public void Env_WithNullOrEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.Env("", "value"));
        Assert.Throws<ArgumentNullException>(() => stage.Env(null!, "value"));
    }

    [Fact]
    public void Env_WithNullValue_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => stage.Env("NODE_ENV", null!));
    }

    [Fact]
    public void Expose_WithValidPort_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.Expose(3000);

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + EXPOSE
    }

    [Fact]
    public void Expose_WithInvalidPort_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => stage.Expose(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => stage.Expose(-1));
    }

    [Fact]
    public void Cmd_WithValidCommand_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");
        var command = new[] { "node", "server.js" };

        // Act
        var result = stage.Cmd(command);

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + CMD
    }

    [Fact]
    public void Cmd_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => stage.Cmd(null!));
    }

    [Fact]
    public void Cmd_WithEmptyCommand_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.Cmd([]));
    }

    [Fact]
    public void User_WithValidUser_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.User("appuser");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + USER
    }

    [Fact]
    public void User_WithNullOrEmptyUser_ThrowsArgumentException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => stage.User(""));
        Assert.Throws<ArgumentNullException>(() => stage.User(null!));
    }

    [Fact]
    public void FluentChaining_WorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node", "20-bullseye")
            .WorkDir("/app")
            .Copy("package*.json", "./")
            .Run("npm ci")
            .Env("NODE_ENV", "production")
            .Expose(3000)
            .Cmd(["node", "server.js"]);

        // Assert
        Assert.Equal(7, stage.Statements.Count); // FROM + WORKDIR + COPY + RUN + ENV + EXPOSE + CMD
    }

    [Fact]
    public void FluentChaining_WithUser_WorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node")
            .WorkDir("/app")
            .Copy(".", ".")
            .User("appuser")
            .Cmd(["node", "server.js"]);

        // Assert
        Assert.Equal(5, stage.Statements.Count); // FROM + WORKDIR + COPY + USER + CMD
    }

    [Fact]
    public void Comment_WithSingleLineComment_AddsStatement()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act
        var result = stage.Comment("This is a comment");

        // Assert
        Assert.Same(stage, result);
        Assert.Equal(2, stage.Statements.Count); // FROM + COMMENT
    }

    [Fact]
    public async Task Comment_WithSingleLineComment_WritesCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        stage.Comment("This is a single-line comment");
        stage.Run("echo hello");
        
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
            # This is a single-line comment
            RUN echo hello

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task Comment_WithMultiLineComment_SplitsCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        var multiLineComment = """
            This is line 1
            This is line 2
            This is line 3
            """;
        stage.Comment(multiLineComment);
        stage.Run("echo hello");
        
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
            # This is line 1
            # This is line 2
            # This is line 3
            RUN echo hello

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task Comment_WithEmptyLinesInMultiLineComment_PreservesEmptyLines()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        var multiLineComment = """
            Section 1 comment

            Section 2 comment
            """;
        stage.Comment(multiLineComment);
        stage.Run("echo hello");
        
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
            # Section 1 comment
            # 
            # Section 2 comment
            RUN echo hello

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task Comment_WithEmptyString_WritesCommentPrefix()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        stage.Comment("");
        stage.Run("echo hello");
        
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
            # 
            RUN echo hello

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void Comment_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => stage.Comment(null!));
    }

    [Fact]
    public async Task Comment_MultipleComments_WritesSequentially()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        stage.Comment("First comment");
        stage.Run("echo first");
        stage.Comment("Second comment");
        stage.Run("echo second");
        
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
            # First comment
            RUN echo first
            # Second comment
            RUN echo second

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public void FluentChaining_WithComment_WorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();

        // Act
        var stage = builder.From("node")
            .Comment("Install dependencies")
            .WorkDir("/app")
            .Copy("package*.json", "./")
            .Run("npm ci")
            .Comment("Copy application files")
            .Copy(".", ".")
            .Cmd(["node", "server.js"]);

        // Assert
        Assert.Equal(8, stage.Statements.Count); // FROM + COMMENT + WORKDIR + COPY + RUN + COMMENT + COPY + CMD
    }

    [Fact]
    public async Task Comment_WithComplexMultiLineComment_FormatsCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine");
        var complexComment = """
            ==========================================
            Build Stage 1: Dependencies
            ==========================================
            Install all required system packages
            and configure the build environment
            """;
        stage.Comment(complexComment);
        stage.Run("apk add build-base");
        
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
            # ==========================================
            # Build Stage 1: Dependencies
            # ==========================================
            # Install all required system packages
            # and configure the build environment
            RUN apk add build-base

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task Comment_AsDockerfileHeader_WorksCorrectly()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        
        // Add comment before FROM
        var stage = builder.From("node:18");
        stage.Statements.Insert(0, new DockerfileCommentStatement("Generated Dockerfile for Node.js application"));
        stage.WorkDir("/app");
        
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await builder.WriteAsync(writer);
        await writer.FlushAsync();

        // Assert
        var content = Encoding.UTF8.GetString(stream.ToArray());
        var expectedContent = """
            # Generated Dockerfile for Node.js application
            FROM node:18
            WORKDIR /app

            """.ReplaceLineEndings("\n");
        
        Assert.Equal(expectedContent, content);
    }
}
