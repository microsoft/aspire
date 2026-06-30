// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetPackageCacheTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Aspire.Cli")]
    [InlineData("Aspire.ProjectTemplates")]
    public async Task PrefetchedPackageMetadataUsesExactMatchAndTimeout(string packageId)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var layout = new LayoutConfiguration
        {
            LayoutPath = workspace.WorkspaceRoot.FullName,
            Components = new LayoutComponents
            {
                Managed = "managed"
            }
        };

        var managedDirectory = workspace.WorkspaceRoot.CreateSubdirectory("managed");
        var managedPath = layout.GetManagedPath();
        Assert.NotNull(managedPath);
        await File.WriteAllTextAsync(managedPath!, string.Empty);

        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = layout
        };

        var executionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0,
                $$"""
                {"packages":[{"id":"{{packageId}}","version":"13.4.0","allVersions":["13.3.0","13.4.0"],"source":"nuget.org"}],"totalHits":1}
                """)
        };

        var cache = new BundleNuGetPackageCache(
            bundleService,
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetPackageCache>.Instance,
            new TestFeatures());

        var packages = packageId switch
        {
            "Aspire.Cli" => await cache.GetCliPackagesAsync(workspace.WorkspaceRoot, prerelease: false, nugetConfigFile: null, CancellationToken.None),
            "Aspire.ProjectTemplates" => await cache.GetTemplatePackagesAsync(workspace.WorkspaceRoot, prerelease: false, nugetConfigFile: null, CancellationToken.None),
            _ => throw new InvalidOperationException()
        };

        Assert.Contains("--exact-match", executionFactory.LastArguments!);
        Assert.Contains("--timeout-seconds", executionFactory.LastArguments!);
        Assert.Equal("1", executionFactory.LastArguments![Array.IndexOf(executionFactory.LastArguments, "--take") + 1]);
        Assert.Equal(NuGetPackageSearchDefaults.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), executionFactory.LastArguments![Array.IndexOf(executionFactory.LastArguments, "--timeout-seconds") + 1]);
        Assert.Collection(
            packages.OrderBy(package => package.Version),
            package =>
            {
                Assert.Equal(packageId, package.Id);
                Assert.Equal("13.3.0", package.Version);
            },
            package =>
            {
                Assert.Equal(packageId, package.Id);
                Assert.Equal("13.4.0", package.Version);
            });
    }

    [Fact]
    public async Task GetPackageVersionsAsync_ExpandsAllVersionsFromExactMatchResult()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var layout = new LayoutConfiguration
        {
            LayoutPath = workspace.WorkspaceRoot.FullName,
            Components = new LayoutComponents
            {
                Managed = "managed"
            }
        };

        var managedDirectory = workspace.WorkspaceRoot.CreateSubdirectory("managed");
        var managedPath = layout.GetManagedPath();
        Assert.NotNull(managedPath);
        await File.WriteAllTextAsync(managedPath!, string.Empty);

        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = layout
        };

        var executionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0,
                """
                {"packages":[{"id":"Aspire.Hosting.Redis","version":"13.3.0","allVersions":["13.3.0","13.2.0"],"source":"nuget.org"}],"totalHits":1}
                """)
        };

        var cache = new BundleNuGetPackageCache(
            bundleService,
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetPackageCache>.Instance,
            new TestFeatures());

        var packages = (await cache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting.Redis",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None)).OrderBy(package => package.Version).ToArray();

        Assert.Collection(
            packages,
            package => Assert.Equal("13.2.0", package.Version),
            package => Assert.Equal("13.3.0", package.Version));
        Assert.True(executionFactory.LastProcessInvocationOptions?.KillOnParentExit);
    }

    [Fact]
    public async Task GetPackageVersionsAsync_MatchesPackageIdCaseInsensitively()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var layout = new LayoutConfiguration
        {
            LayoutPath = workspace.WorkspaceRoot.FullName,
            Components = new LayoutComponents
            {
                Managed = "managed"
            }
        };

        var managedDirectory = workspace.WorkspaceRoot.CreateSubdirectory("managed");
        var managedPath = layout.GetManagedPath();
        Assert.NotNull(managedPath);
        await File.WriteAllTextAsync(managedPath!, string.Empty);

        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = layout
        };

        var executionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, _) => (0,
                """
                {"packages":[{"id":"Aspire.Hosting.Redis","version":"13.3.0","allVersions":["13.3.0"],"source":"nuget.org"}],"totalHits":1}
                """)
        };

        var cache = new BundleNuGetPackageCache(
            bundleService,
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetPackageCache>.Instance,
            new TestFeatures());

        var packages = await cache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "aspire.hosting.redis",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None);

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.3.0", package.Version);
            });
    }
}
