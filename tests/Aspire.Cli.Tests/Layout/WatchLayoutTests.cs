// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Tests.LayoutTests;

/// <summary>
/// Covers discovery of the bundled watch tool (<c>Microsoft.DotNet.HotReload.Watch.Aspire</c>) from a bundle layout. 
/// </summary>
public class WatchLayoutTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TryDiscoverWatchToolFromDirectory_FindsEntryPoint_WhenWatchDirectoryExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var baseDir = workspace.WorkspaceRoot.FullName;

        var watchDir = Path.Combine(baseDir, BundleDiscovery.WatchDirectoryName);
        Directory.CreateDirectory(watchDir);
        var entryPoint = Path.Combine(watchDir, BundleDiscovery.WatchToolDllName);
        File.WriteAllText(entryPoint, "stub");

        Assert.True(BundleDiscovery.TryDiscoverWatchToolFromDirectory(baseDir, out var watchToolPath));
        Assert.Equal(entryPoint, watchToolPath);
    }

    [Fact]
    public void TryDiscoverWatchToolFromDirectory_ReturnsFalse_WhenWatchDirectoryMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var baseDir = workspace.WorkspaceRoot.FullName;

        Assert.False(BundleDiscovery.TryDiscoverWatchToolFromDirectory(baseDir, out var watchToolPath));
        Assert.Null(watchToolPath);
    }

    [Fact]
    public void GetWatchToolPath_ReturnsEntryPoint_WhenWatchComponentPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;

        var watchDir = Path.Combine(layoutRoot, BundleDiscovery.WatchDirectoryName);
        Directory.CreateDirectory(watchDir);
        var entryPoint = Path.Combine(watchDir, BundleDiscovery.WatchToolDllName);
        File.WriteAllText(entryPoint, "stub");

        var layout = new LayoutConfiguration { LayoutPath = layoutRoot };

        Assert.Equal(entryPoint, layout.GetWatchToolPath());
    }

    [Fact]
    public void GetWatchToolPath_ReturnsNull_WhenWatchEntryPointMissing()
    {
        // Externally-supplied/legacy layouts (and the inner-loop dev path) may lack a watch/ directory.
        // GetWatchToolPath must return null in that case so callers don't inject a dangling
        // ASPIRE_WATCH_TOOL_PATH pointing at a non-existent DLL.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layout = new LayoutConfiguration { LayoutPath = workspace.WorkspaceRoot.FullName };

        Assert.Null(layout.GetWatchToolPath());
    }

    [Fact]
    public void GetWatchToolPath_ReturnsNull_WhenLayoutPathUnset()
    {
        var layout = new LayoutConfiguration();

        Assert.Null(layout.GetWatchToolPath());
    }
}

/// <summary>
/// Covers the inner-loop dev fallback that resolves the watch tool from the NuGet global
/// packages cache (used when running <c>dotnet run --project src/Aspire.Cli</c> with no CLI
/// bundle present). These tests mutate the process-wide <c>NUGET_PACKAGES</c> variable, which
/// <see cref="BundleDiscovery.TryGetWatchToolPathFromNuGetCache(string?)"/> reads, so they join
/// <see cref="EnvVarMutatingTestCollection"/> to serialize against other env-var-mutating suites.
/// </summary>
[Collection(EnvVarMutatingTestCollection.Name)]
public class WatchNuGetCacheDiscoveryTests(ITestOutputHelper outputHelper)
{
    // Intentionally a far-into-the-future package version for testing
    private const string TestWatchPackageVersion = "20.0.100";

    [Fact]
    public void TryGetWatchToolPathFromNuGetCache_FindsEntryPoint_ForPinnedVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cacheRoot = workspace.WorkspaceRoot.FullName;

        var toolsDir = Path.Combine(cacheRoot, BundleDiscovery.WatchToolNugetCacheFolder, TestWatchPackageVersion, "tools", BundleDiscovery.WatchToolDotNetVersion, "any");
        Directory.CreateDirectory(toolsDir);
        var entryPoint = Path.Combine(toolsDir, BundleDiscovery.WatchToolDllName);
        File.WriteAllText(entryPoint, "stub");

        var original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", cacheRoot);
            Assert.Equal(entryPoint, BundleDiscovery.TryGetWatchToolPathFromNuGetCache(TestWatchPackageVersion));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", original);
        }
    }

    [Fact]
    public void TryGetWatchToolPathFromNuGetCache_FindsPrereleaseEntryPoint_WhenVersionUnspecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cacheRoot = workspace.WorkspaceRoot.FullName;
        var prereleaseVersion = $"{TestWatchPackageVersion}-preview.1";

        var toolsDir = Path.Combine(cacheRoot, BundleDiscovery.WatchToolNugetCacheFolder, prereleaseVersion, "tools", BundleDiscovery.WatchToolDotNetVersion, "any");
        Directory.CreateDirectory(toolsDir);
        var entryPoint = Path.Combine(toolsDir, BundleDiscovery.WatchToolDllName);
        File.WriteAllText(entryPoint, "stub");

        var original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", cacheRoot);
            Assert.Equal(toolsDir, BundleDiscovery.TryGetWatchToolDirectoryFromNuGetCache(version: null));
            Assert.Equal(entryPoint, BundleDiscovery.TryGetWatchToolPathFromNuGetCache(version: null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", original);
        }
    }

    [Fact]
    public void TryGetWatchToolPathFromNuGetCache_ReturnsNull_WhenPackageMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cacheRoot = workspace.WorkspaceRoot.FullName;

        var original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", cacheRoot);
            Assert.Null(BundleDiscovery.TryGetWatchToolPathFromNuGetCache(TestWatchPackageVersion));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", original);
        }
    }
}
