// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class FirstTimeUseNoticeSentinelTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Exists_WhenSentinelFileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new FirstTimeUseNoticeSentinel(workspace.WorkspaceRoot.FullName);

        // Act
        var exists = sentinel.Exists();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void Exists_WhenSentinelFileExists_ReturnsTrue()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cliDir = Path.Combine(workspace.WorkspaceRoot.FullName, "cli");
        Directory.CreateDirectory(cliDir);
        var sentinelFilePath = Path.Combine(cliDir, "cli.firstUseSentinel");
        File.WriteAllText(sentinelFilePath, string.Empty);
        var sentinel = new FirstTimeUseNoticeSentinel(workspace.WorkspaceRoot.FullName);

        // Act
        var exists = sentinel.Exists();

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void CreateIfNotExists_WhenSentinelFileDoesNotExist_CreatesFile()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new FirstTimeUseNoticeSentinel(workspace.WorkspaceRoot.FullName);

        // Act
        sentinel.CreateIfNotExists();

        // Assert
        var sentinelFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli", "cli.firstUseSentinel");
        Assert.True(File.Exists(sentinelFilePath));
    }

    [Fact]
    public void CreateIfNotExists_WhenSentinelFileAlreadyExists_DoesNotThrow()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cliDir = Path.Combine(workspace.WorkspaceRoot.FullName, "cli");
        Directory.CreateDirectory(cliDir);
        var sentinelFilePath = Path.Combine(cliDir, "cli.firstUseSentinel");
        File.WriteAllText(sentinelFilePath, "existing content");
        var sentinel = new FirstTimeUseNoticeSentinel(workspace.WorkspaceRoot.FullName);

        // Act
        var exception = Record.Exception(sentinel.CreateIfNotExists);

        // Assert
        Assert.Null(exception);
        Assert.True(File.Exists(sentinelFilePath));
    }

    [Fact]
    public void CreateIfNotExists_WhenDirectoryDoesNotExist_CreatesDirectoryAndFile()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nonExistentDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "non-existent-dir");
        var sentinel = new FirstTimeUseNoticeSentinel(nonExistentDirectory);

        // Act
        sentinel.CreateIfNotExists();

        // Assert
        var cliDir = Path.Combine(nonExistentDirectory, "cli");
        var sentinelFilePath = Path.Combine(cliDir, "cli.firstUseSentinel");
        Assert.True(Directory.Exists(cliDir));
        Assert.True(File.Exists(sentinelFilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNullOrWhitespaceDirectory_ThrowsArgumentException(string? directory)
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => new FirstTimeUseNoticeSentinel(directory!));
    }
}
