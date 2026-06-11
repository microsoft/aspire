// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Docker.Tests;

public class EnvFileTests
{
    [Fact]
    public void Add_WithOnlyIfMissingTrue_DoesNotAddDuplicate()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and try to add the same key with onlyIfMissing=true
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value2", "New comment", onlyIfMissing: true);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var keyLines = lines.Where(l => l.StartsWith("KEY1=")).ToArray();

        // Should still have only one KEY1 line with original value
        Assert.Single(keyLines);
        Assert.Equal("KEY1=value1", keyLines[0]);
    }

    [Fact]
    public void Add_WithOnlyIfMissingFalse_UpdatesExistingKey()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and try to add the same key with onlyIfMissing=false
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value2", "New comment", onlyIfMissing: false);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var keyLines = lines.Where(l => l.StartsWith("KEY1=")).ToArray();

        // Should still have only one KEY1 line, but with updated value
        Assert.Single(keyLines);
        Assert.Equal("KEY1=value2", keyLines[0]);
    }

    [Fact]
    public void Add_WithOnlyIfMissingFalse_UpdatesImageNameWithoutDuplication()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Create initial .env file simulating a project resource
        File.WriteAllLines(envFilePath, [
            "# Default container port for project1",
            "PROJECT1_PORT=8080",
            "",
            "# Container image name for project1",
            "PROJECT1_IMAGE=project1:latest",
            ""
        ]);

        // Load the file
        var envFile = EnvFile.Load(envFilePath);

        // Add PORT with onlyIfMissing=true (should be skipped since it exists)
        envFile.Add("PROJECT1_PORT", "8080", "Default container port for project1", onlyIfMissing: true);

        // Add IMAGE with onlyIfMissing=false (should update the existing value)
        envFile.Add("PROJECT1_IMAGE", "project1:1.0.0", "Container image name for project1", onlyIfMissing: false);

        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        var imageLines = lines.Where(l => l.StartsWith("PROJECT1_IMAGE=")).ToArray();

        // Should have exactly one IMAGE line with the new value
        Assert.Single(imageLines);
        Assert.Equal("PROJECT1_IMAGE=project1:1.0.0", imageLines[0]);

        // PORT should also still be present once
        var portLines = lines.Where(l => l.StartsWith("PROJECT1_PORT=")).ToArray();
        Assert.Single(portLines);
        Assert.Equal("PROJECT1_PORT=8080", portLines[0]);
    }

    [Fact]
    public void Add_NewKey_AddsToFile()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Create initial .env file
        File.WriteAllLines(envFilePath, [
            "# Comment for KEY1",
            "KEY1=value1",
            ""
        ]);

        // Load and add a new key
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY2", "value2", "Comment for KEY2", onlyIfMissing: true);
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);

        // Should have both keys
        Assert.Contains("KEY1=value1", lines);
        Assert.Contains("KEY2=value2", lines);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyEnvFile()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Create empty file
        File.WriteAllText(envFilePath, string.Empty);

        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value1", "Comment");
        envFile.Save();

        var lines = File.ReadAllLines(envFilePath);
        Assert.Contains("KEY1=value1", lines);
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyEnvFile()
    {
        using var tempDir = new TestTempDirectory();
        var envFilePath = Path.Combine(tempDir.Path, ".env");

        // Don't create the file
        var envFile = EnvFile.Load(envFilePath);
        envFile.Add("KEY1", "value1", "Comment");
        envFile.Save();

        Assert.True(File.Exists(envFilePath));
        var lines = File.ReadAllLines(envFilePath);
        Assert.Contains("KEY1=value1", lines);
    }
}
