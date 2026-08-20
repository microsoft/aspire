// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Semver;

namespace Aspire.Cli.Tests.LayoutTests;

public class LayoutConfigurationTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void LayoutComponentValuesRemainStable()
    {
        Assert.Equal(0, (int)LayoutComponent.Cli);
        Assert.Equal(1, (int)LayoutComponent.Dcp);
        Assert.Equal(2, (int)LayoutComponent.Managed);
        Assert.Equal(3, (int)LayoutComponent.Dashboard);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("13.5.3", false)]
    [InlineData("13.5.99", false)]
    [InlineData("13.6.0-0", true)]
    [InlineData("13.6.0-preview.1", true)]
    [InlineData("13.6.0", true)]
    [InlineData("13.6.0+build.1", true)]
    [InlineData("14.0.0", true)]
    public void SupportsNativeDashboard_RequiresCompatibleHostingVersion(string? hostingVersion, bool expected)
    {
        var version = hostingVersion is not null ? SemVersion.Parse(hostingVersion) : null;

        Assert.Equal(expected, DashboardLaunchHelper.SupportsNativeDashboard(version));
    }

    [Theory]
    [InlineData(true, true, true, LayoutComponent.Dashboard)]
    [InlineData(true, true, false, LayoutComponent.Dashboard)]
    [InlineData(true, false, true, LayoutComponent.Managed)]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, true, LayoutComponent.Managed)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, LayoutComponent.Managed)]
    [InlineData(false, false, false, null)]
    public void GetDashboardPath_SelectsCompatibleExistingExecutable(bool supportsNativeDashboard, bool nativeExists, bool managedExists, LayoutComponent? expectedComponent)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layout = new LayoutConfiguration { LayoutPath = workspace.WorkspaceRoot.FullName };
        var dashboardPath = Assert.IsType<string>(layout.GetDashboardPath());
        var managedPath = Assert.IsType<string>(layout.GetManagedPath());
        if (nativeExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dashboardPath)!);
            File.WriteAllText(dashboardPath, string.Empty);
        }
        if (managedExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
            File.WriteAllText(managedPath, string.Empty);
        }

        var expected = expectedComponent switch
        {
            LayoutComponent.Dashboard => dashboardPath,
            LayoutComponent.Managed => managedPath,
            _ => null
        };

        Assert.Equal(expected, DashboardLaunchHelper.GetDashboardPath(layout, supportsNativeDashboard));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetDashboardPath_WithoutConfiguredLayout_ReturnsNull(bool supportsNativeDashboard)
    {
        Assert.Null(DashboardLaunchHelper.GetDashboardPath(new LayoutConfiguration(), supportsNativeDashboard));
    }
}
