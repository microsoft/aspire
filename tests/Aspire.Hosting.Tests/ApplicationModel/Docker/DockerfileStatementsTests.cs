// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using System.Text;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileStatementsTests
{
    [Fact]
    public async Task FromStatement_WithoutStage_WritesCorrectFormat()
    {
        // Arrange - test via public API since statements are internal
        var builder = new DockerfileBuilder();
        var stage = builder.From("node:20-bullseye");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node:20-bullseye\n", result);
    }

    [Fact]
    public async Task FromStatement_WithStage_WritesCorrectFormat()
    {
        // Arrange - test via public API
        var builder = new DockerfileBuilder();
        var stage = builder.From("node:20-bullseye", "builder");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node:20-bullseye AS builder\n", result);
    }

    [Fact]
    public async Task WorkDirStatement_WritesCorrectFormat()
    {
        // Arrange - test via public API
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").WorkDir("/app");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nWORKDIR /app\n", result);
    }

    [Fact]
    public async Task RunStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").Run("npm install");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nRUN npm install\n", result);
    }

    [Fact]
    public async Task RunStatement_WithComplexCommand_WritesCorrectFormat()
    {
        // Arrange
        var command = "apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*";
        var builder = new DockerfileBuilder();
        var stage = builder.From("ubuntu").Run(command);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal($"FROM ubuntu\nRUN {command}\n", result);
    }

    [Fact]
    public async Task CopyStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").Copy("package*.json", "./");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nCOPY package*.json ./\n", result);
    }

    [Fact]
    public async Task CopyFromStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("nginx").CopyFrom("builder", "/app/dist", "/srv");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM nginx\nCOPY --from=builder /app/dist /srv\n", result);
    }

    [Fact]
    public async Task EnvStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").Env("NODE_ENV", "production");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nENV NODE_ENV=production\n", result);
    }

    [Fact]
    public async Task EnvStatement_WithEmptyValue_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine").Env("PATH", "");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM alpine\nENV PATH=\n", result);
    }

    [Fact]
    public async Task ExposeStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").Expose(3000);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nEXPOSE 3000\n", result);
    }

    [Fact]
    public async Task CmdStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").Cmd(["node", "server.js"]);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\n" + """CMD ["node","server.js"]""" + "\n", result);
    }

    [Fact]
    public async Task CmdStatement_WithSingleCommand_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("nginx").Cmd(["nginx"]);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM nginx\n" + """CMD ["nginx"]""" + "\n", result);
    }

    [Fact]
    public async Task CmdStatement_WithComplexCommand_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("caddy").Cmd(["cmd", "run", "--config", "/etc/caddy/caddy.json"]);
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM caddy\n" + """CMD ["cmd","run","--config","/etc/caddy/caddy.json"]""" + "\n", result);
    }

    [Fact]
    public async Task UserStatement_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("node").User("appuser");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM node\nUSER appuser\n", result);
    }

    [Fact]
    public async Task UserStatement_WithUID_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine").User("1000");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM alpine\nUSER 1000\n", result);
    }

    [Fact]
    public async Task UserStatement_WithUIDAndGID_WritesCorrectFormat()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var stage = builder.From("alpine").User("1000:1000");
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

        // Act
        await stage.WriteStatementAsync(writer);
        await writer.FlushAsync();

        // Assert
        var result = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("FROM alpine\nUSER 1000:1000\n", result);
    }
}