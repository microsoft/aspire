// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RestorePackagesAsync_UsesWorkspaceAspireDirectoryForRestoreArtifacts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetService>.Instance);

        var libsPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "packages", "restore");
        var restoreDirectory = Directory.GetParent(libsPath)!.FullName;

        Assert.StartsWith(restoreRoot, libsPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invocations.Count);
        Assert.Equal(Path.Combine(restoreDirectory, "obj"), GetArgumentValue(invocations[0], "--output"));
        Assert.Equal(libsPath, GetArgumentValue(invocations[1], "--output"));
        Assert.Equal(Path.Combine(restoreDirectory, "obj", "project.assets.json"), GetArgumentValue(invocations[1], "--assets"));
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentSources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetService>.Instance);

        var libsPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-a/index.json"],
            workingDirectory: appHostDirectory.FullName);

        var libsPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-b/index.json"],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(libsPathA, libsPathB);
    }

    [Fact]
    public async Task RestorePackagesAsync_PassesNuGetConfigToRestore()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(nugetConfigPath, "<configuration />");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetService>.Instance);

        await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetConfigPath: nugetConfigPath);

        Assert.Equal(nugetConfigPath, GetArgumentValue(invocations[0], "--nuget-config"));
    }

    [Fact]
    public async Task RestorePackagesAsync_SharesRestoreCacheAcrossAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetService>.Instance);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "packages", "restore");

        // Same packages + sources across two apphosts in one workspace should share the cache.
        var sharedLibsFirst = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: firstAppHost.FullName);
        var sharedLibsSecond = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, sharedLibsFirst, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(restoreRoot, sharedLibsSecond, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sharedLibsFirst, sharedLibsSecond);

        // Different package sets must NOT collide even when workspace is shared.
        var divergedLibs = await service.RestorePackagesAsync(
            [("Aspire.Hosting.Python", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, divergedLibs, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(sharedLibsSecond, divergedLibs);
    }

    private static string GetArgumentValue(string[] arguments, string optionName)
    {
        var optionIndex = Array.IndexOf(arguments, optionName);
        Assert.True(optionIndex >= 0 && optionIndex < arguments.Length - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
