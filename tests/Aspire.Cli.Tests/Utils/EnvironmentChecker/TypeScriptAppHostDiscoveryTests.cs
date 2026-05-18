// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils.EnvironmentChecks;

public class TypeScriptAppHostDiscoveryTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TryDiscover_ReturnsNull_WhenNoAppHostExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.Null(result);
    }

    [Fact]
    public void TryDiscover_FindsAppHostInWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteAppHostFiles(workspace.WorkspaceRoot);

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.NotNull(result);
        Assert.Equal(workspace.WorkspaceRoot.FullName, result!.AppHostDirectory.FullName);
        Assert.Equal("apphost.ts", result.AppHostFile.Name);
    }

    [Fact]
    public void TryDiscover_FindsAppHostInImmediateChildDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WriteAppHostFiles(appHostDir);

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.NotNull(result);
        Assert.Equal(appHostDir.FullName, result!.AppHostDirectory.FullName);
    }

    [Fact]
    public void TryDiscover_SkipsHiddenAndNoiseDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nodeModules = workspace.CreateDirectory("node_modules");
        WriteAppHostFiles(nodeModules);

        var binDir = workspace.CreateDirectory("bin");
        WriteAppHostFiles(binDir);

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.Null(result);
    }

    [Fact]
    public void TryDiscover_RequiresBothAppHostAndPackageJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), string.Empty);

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.Null(result);
    }

    [Fact]
    public void TryDiscover_UsesSettingsLanguageHint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WriteAppHostFiles(appHostDir);

        // Override the empty settings file created by TemporaryWorkspace.Create.
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """{ "language": "typescript/nodejs" }""");

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.NotNull(result);
        Assert.Equal(appHostDir.FullName, result!.AppHostDirectory.FullName);
    }

    [Fact]
    public void TryDiscover_IgnoresSettingsWithUnknownLanguage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """{ "language": "csharp" }""");

        var result = TypeScriptAppHostDiscovery.TryDiscover(workspace.WorkspaceRoot);

        Assert.Null(result);
    }

    [Fact]
    public void TryDiscover_ReturnsNull_WhenWorkingDirectoryDoesNotExist()
    {
        var nonexistent = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        var result = TypeScriptAppHostDiscovery.TryDiscover(nonexistent);

        Assert.Null(result);
    }

    private static void WriteAppHostFiles(DirectoryInfo directory)
    {
        File.WriteAllText(Path.Combine(directory.FullName, "apphost.ts"), string.Empty);
        File.WriteAllText(Path.Combine(directory.FullName, "package.json"), "{}");
    }
}
