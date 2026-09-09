// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Agents;

public class CopilotAppInstallationDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetInstallationMarker_WhenWindowsAppExecutableExists_ReturnsPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localAppData = workspace.CreateDirectory("local-app-data");
        var appDirectory = Directory.CreateDirectory(
            Path.Combine(localAppData.FullName, "Programs", "GitHub Copilot"));
        await File.WriteAllTextAsync(
            Path.Combine(appDirectory.FullName, "github.exe"),
            string.Empty,
            TestContext.Current.CancellationToken);
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = localAppData.FullName,
        });
        var executablePath = Path.Combine(appDirectory.FullName, "github.exe");

        Assert.Equal(executablePath, CreateDetector(environment, workspace).GetInstallationMarker());
    }

    [Fact]
    public void GetInstallationMarker_WhenUserMacOSAppBundleExists_ReturnsPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appBundlePath = Path.Combine(workspace.WorkspaceRoot.FullName, "Applications", "GitHub Copilot.app");
        Directory.CreateDirectory(appBundlePath);

        Assert.Equal(appBundlePath, CreateDetector(TestEnvironment.CreateMacOS(), workspace).GetInstallationMarker());
    }

    [Fact]
    public void GetInstallationMarker_WhenRuntimeMarkerIsPresent_ReturnsEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [CopilotAppInstallationDetector.AgentEnvironmentVariable] =
                CopilotAppInstallationDetector.AgentEnvironmentValue,
        });

        Assert.Equal(
            CopilotAppInstallationDetector.AgentEnvironmentVariable,
            CreateDetector(environment, workspace).GetInstallationMarker());
    }

    [Fact]
    public async Task GetInstallationMarker_WhenLinuxDesktopEntryExists_ReturnsPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applicationsDirectory = workspace.CreateDirectory(
            Path.Combine(".local", "share", "applications"));
        await File.WriteAllTextAsync(
            Path.Combine(applicationsDirectory.FullName, "github-copilot.desktop"),
            """
            [Desktop Entry]
            Name=GitHub Copilot
            Exec=/opt/github-copilot/github
            """,
            TestContext.Current.CancellationToken);
        var desktopEntryPath = Path.Combine(applicationsDirectory.FullName, "github-copilot.desktop");

        Assert.Equal(
            desktopEntryPath,
            CreateDetector(TestEnvironment.CreateLinux(), workspace).GetInstallationMarker());
    }

    [Fact]
    public void GetLinuxApplicationDirectories_WithoutXdgOverrides_UsesFreedesktopDefaults()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = CreateDetector(TestEnvironment.CreateLinux(), workspace);
        var rootDirectory = Path.DirectorySeparatorChar.ToString();

        Assert.Equal(
            [
                Path.Combine(workspace.WorkspaceRoot.FullName, ".local", "share", "applications"),
                Path.Combine(rootDirectory, "usr", "local", "share", "applications"),
                Path.Combine(rootDirectory, "usr", "share", "applications"),
            ],
            detector.GetLinuxApplicationDirectories());
    }

    [Fact]
    public void GetLinuxApplicationDirectories_WithXdgOverrides_UsesAbsoluteEntries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dataHome = workspace.CreateDirectory("xdg-data-home");
        var firstDataDirectory = workspace.CreateDirectory("xdg-data-first");
        var secondDataDirectory = workspace.CreateDirectory("xdg-data-second");
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = dataHome.FullName,
            ["XDG_DATA_DIRS"] = string.Join(
                Path.PathSeparator,
                firstDataDirectory.FullName,
                "relative-directory",
                secondDataDirectory.FullName),
        });
        var detector = CreateDetector(environment, workspace);

        Assert.Equal(
            [
                Path.Combine(dataHome.FullName, "applications"),
                Path.Combine(firstDataDirectory.FullName, "applications"),
                Path.Combine(secondDataDirectory.FullName, "applications"),
            ],
            detector.GetLinuxApplicationDirectories());
    }

    [Fact]
    public void GetInstallationMarker_WithoutInstallationOrRuntimeMarker_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dataHome = workspace.CreateDirectory("xdg-data-home");
        var dataDirectory = workspace.CreateDirectory("xdg-data-dir");
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            ["XDG_DATA_HOME"] = dataHome.FullName,
            ["XDG_DATA_DIRS"] = dataDirectory.FullName,
        });
        Assert.Null(CreateDetector(environment, workspace).GetInstallationMarker());
    }

    private static CopilotAppInstallationDetector CreateDetector(
        IEnvironment environment,
        TemporaryWorkspace workspace)
    {
        return new(
            environment,
            TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                homeDirectory: workspace.WorkspaceRoot));
    }
}
