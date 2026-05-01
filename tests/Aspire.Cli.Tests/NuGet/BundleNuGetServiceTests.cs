// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Mcp;
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
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var manifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore");
        var restoreDirectory = Directory.GetParent(manifestPath)!.FullName;

        Assert.StartsWith(restoreRoot, manifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invocations.Count);
        Assert.Equal(Path.Combine(restoreDirectory, "obj"), GetArgumentValue(invocations[0], "--output"));
        Assert.Equal("manifest", invocations[1][1]);
        Assert.Equal(manifestPath, GetArgumentValue(invocations[1], "--output"));
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
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-a/index.json"],
            workingDirectory: appHostDirectory.FullName);

        var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-b/index.json"],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA, resultB);
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
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetConfigPath: nugetConfigPath);

        Assert.Equal(nugetConfigPath, GetArgumentValue(invocations[0], "--nuget-config"));
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesCachedManifestWithoutRunningHelper()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            "integration-package-probe-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{}");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task RestorePackagesAsync_RegeneratesCachedManifestWhenManifestIsInvalid()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            "integration-package-probe-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ invalid json");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                invocations.Add(args.ToArray());
                if (args.Contains("manifest"))
                {
                    File.WriteAllText(manifestPath, """{"managedAssemblies":[],"nativeLibraries":[]}""");
                }
            }
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
        Assert.Equal(2, invocations.Count);
        Assert.Equal("restore", invocations[0][1]);
        Assert.Equal("manifest", invocations[1][1]);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsWhenManagedHelperChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, "v1");

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        File.WriteAllText(managedPath, "v2-changed");

        var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA, resultB);
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
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore");

        // Same packages + sources across two apphosts in one workspace should share the cache.
        var sharedManifestFirst = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: firstAppHost.FullName);
        var sharedManifestSecond = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, sharedManifestFirst, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(restoreRoot, sharedManifestSecond, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sharedManifestFirst, sharedManifestSecond);

        // Different package sets must NOT collide even when workspace is shared.
        var divergedManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.Python", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, divergedManifest, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(sharedManifestSecond, divergedManifest);
    }

    [Fact]
    public async Task RestorePackagesAsync_IgnoresLockedLegacyLibsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var restoreDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore", packageHash);
        var legacyLibsDirectory = Path.Combine(restoreDirectory, "libs");
        Directory.CreateDirectory(legacyLibsDirectory);
        var lockedFilePath = Path.Combine(legacyLibsDirectory, "Microsoft.Extensions.DependencyInjection.xml");
        File.WriteAllText(lockedFilePath, "legacy");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        using var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(Path.Combine(restoreDirectory, "integration-package-probe-manifest.json"), result);
        Assert.Equal(2, invocations.Count);
        Assert.DoesNotContain(invocations, args => args.Contains("layout"));
        Assert.Equal("manifest", invocations[1][1]);
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
