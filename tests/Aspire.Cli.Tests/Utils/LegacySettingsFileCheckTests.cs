// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils;

public class LegacySettingsFileCheckTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CheckAsync_WithLegacySettingsFile_ReturnsWarning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var legacyDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "settings.json"), "{}");

        // Use a subdirectory as working dir so the legacy file is found via walk-up.
        // The walk-up stops when it finds the legacy file at workspace root.
        var subDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src"));
        subDir.Create();

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(subDir);
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal(LegacySettingsFileCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Contains(".aspire", result.Message);
        Assert.Contains("settings.json", result.Message);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task CheckAsync_WithModernConfigFile_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName),
            """{ "sdk": { "version": "13.0.0" } }""");

        var executionContext = workspace.CreateExecutionContext();
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithBothFiles_ReturnsEmpty()
    {
        // If both legacy and modern files exist at the same level, no warning needed
        // because the modern config takes precedence.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var legacyDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "settings.json"), "{}");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName),
            """{ "sdk": { "version": "13.0.0" } }""");

        var executionContext = workspace.CreateExecutionContext();
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithModernConfigBoundary_AndNoLegacyFiles_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Place a modern config at the workspace root to prevent the walk-up from
        // escaping into the real filesystem where a legacy file might exist.
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName),
            "{}");

        // Use a subdirectory as working dir so the walk-up has room to traverse
        // without immediately finding the boundary config.
        var subDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src"));
        subDir.Create();

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(subDir);
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithLegacyFileInParentDirectory_ReturnsWarning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Place legacy settings at the workspace root level
        var legacyDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "settings.json"), "{}");

        // Use a nested subdirectory as working dir — the walk-up finds the legacy
        // file at workspace root and returns a warning.
        var subDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "MyProject"));
        subDir.Create();

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(subDir);
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
    }

    [Fact]
    public async Task CheckAsync_WithModernConfigInParent_DoesNotWarnAboutLegacyInGrandparent()
    {
        // If a modern config exists between the working dir and the legacy file,
        // the walk-up should stop at the modern config.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Put legacy file in workspace root
        var legacyDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "settings.json"), "{}");

        // Put modern config in a child directory
        var childDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "project"));
        childDir.Create();
        await File.WriteAllTextAsync(
            Path.Combine(childDir.FullName, AspireConfigFile.FileName),
            """{ "sdk": { "version": "13.0.0" } }""");

        // Working dir is under the child directory
        var workingDir = new DirectoryInfo(Path.Combine(childDir.FullName, "src"));
        workingDir.Create();

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workingDir);
        var check = new LegacySettingsFileCheck(executionContext);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public void Order_Is101()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = workspace.CreateExecutionContext();
        var check = new LegacySettingsFileCheck(executionContext);

        Assert.Equal(101, check.Order);
    }
}
