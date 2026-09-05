// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetPackageCacheTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NonAspireCliPackagesWillNotBeConsidered()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("CommunityToolkit.Aspire.Hosting.Foo", "9.4.0-xyz", "nuget.org", ["9.4.0-xyz"]),
                        new("Aspire.Cli", "9.4.0-preview", "nuget.org", ["9.4.0-preview"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetCliPackagesAsync(workspace.WorkspaceRoot, prerelease: true, nugetConfigFile: null, CancellationToken.None).DefaultTimeout();

        Assert.Collection(
            packages,
            package => Assert.Equal("Aspire.Cli", package.Id)
        );
    }

    [Fact]
    public async Task DeprecatedPackagesAreFilteredByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("Aspire.Hosting.Redis", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.Dapr", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.GitHub.Models", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.NodeJs", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.PostgreSQL", "9.4.0", "nuget.org", ["9.4.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should include regular packages but exclude deprecated Dapr package
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.Contains("Aspire.Hosting.PostgreSQL", packageIds);
        Assert.DoesNotContain("Aspire.Hosting.Dapr", packageIds);
        Assert.DoesNotContain("Aspire.Hosting.GitHub.Models", packageIds);
        Assert.DoesNotContain("Aspire.Hosting.NodeJs", packageIds);
    }

    [Fact]
    public async Task DeprecatedPackagesAreIncludedWhenShowDeprecatedPackagesEnabled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            // Enable showing deprecated packages
            configure.EnabledFeatures = [Aspire.Cli.KnownFeatures.ShowDeprecatedPackages];

            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("Aspire.Hosting.Redis", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.Dapr", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.GitHub.Models", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.NodeJs", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.PostgreSQL", "9.4.0", "nuget.org", ["9.4.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should include all packages including deprecated Dapr package when showing deprecated is enabled
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.Contains("Aspire.Hosting.PostgreSQL", packageIds);
        Assert.Contains("Aspire.Hosting.Dapr", packageIds);
        Assert.Contains("Aspire.Hosting.GitHub.Models", packageIds);
        Assert.Contains("Aspire.Hosting.NodeJs", packageIds);
    }

    [Fact]
    public async Task CustomFilterBypassesDeprecatedPackageFiltering()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("Aspire.Hosting.Redis", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.Dapr", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Other.Package", "9.4.0", "nuget.org", ["9.4.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);

        // Use a custom filter that includes all packages containing "Dapr"
        var packages = await nuGetPackageCache.GetPackagesAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting",
            filter: id => id.Contains("Dapr", StringComparison.OrdinalIgnoreCase),
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None).DefaultTimeout();

        // Custom filter should bypass deprecated package filtering
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Dapr", packageIds);
        Assert.DoesNotContain("Aspire.Hosting.Redis", packageIds);
        Assert.DoesNotContain("Other.Package", packageIds);
    }

    [Fact]
    public async Task DeprecatedPackageFilteringIsCaseInsensitive()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("aspire.hosting.dapr", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("ASPIRE.HOSTING.DAPR", "9.4.0", "nuget.org", ["9.4.0"]),
                        new("Aspire.Hosting.Redis", "9.4.0", "nuget.org", ["9.4.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should filter out all case variations of deprecated package
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.DoesNotContain("aspire.hosting.dapr", packageIds);
        Assert.DoesNotContain("ASPIRE.HOSTING.DAPR", packageIds);
    }

    [Fact]
    public async Task AnalyzerPackageIsFilteredFromDefaultPackageSearch()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("Aspire.Hosting.Redis", "13.4.0", "nuget.org", ["13.4.0"]),
                        new("Aspire.Hosting.Integration.Analyzers", "13.4.0", "nuget.org", ["13.4.0"]),
                        new("Aspire.Hosting.PostgreSQL", "13.4.0", "nuget.org", ["13.4.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", filter: null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        Assert.Collection(
            packages.Select(p => p.Id),
            id => Assert.Equal("Aspire.Hosting.Redis", id),
            id => Assert.Equal("Aspire.Hosting.PostgreSQL", id));
    }

    [Fact]
    public async Task GetPackageVersionsAsync_UsesExactMatchSearch()
    {
        int observedTake = -1;
        bool? observedExactMatch = null;
        bool? observedUseCache = null;

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (query, exactMatch, _, take, useCache, _, _, _, _) =>
                    {
                        observedTake = take;
                        observedExactMatch = exactMatch;
                        observedUseCache = useCache;
                        return Task.FromResult<IReadOnlyList<NuGetSearchResult>>(query switch
                        {
                            "Aspire.Hosting.Redis" =>
                            [
                                new("Aspire.Hosting.Redis", "13.3.0", "nuget.org", ["13.3.0", "13.2.0"]),
                                new("Aspire.Hosting.Redis", "14.0.0", "private-feed", ["14.0.0"])
                            ],
                            _ => []
                        });
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = (await nuGetPackageCache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting.Redis",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None)).OrderBy(package => package.Version).ToArray();

        Assert.Equal(1000, observedTake);
        Assert.True(observedExactMatch);
        Assert.True(observedUseCache);
        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("13.2.0", package.Version);
                Assert.Equal("nuget.org", package.Source);
            },
            package =>
            {
                Assert.Equal("13.3.0", package.Version);
                Assert.Equal("nuget.org", package.Source);
            },
            package =>
            {
                Assert.Equal("14.0.0", package.Version);
                Assert.Equal("private-feed", package.Source);
            });
    }

    private static INuGetPackageCache CreateCache(IServiceProvider provider) =>
        new BundleNuGetPackageCache(
            provider.GetRequiredService<INuGetClient>(),
            provider.GetRequiredService<AspireCliTelemetry>(),
            provider.GetRequiredService<IFeatures>());
}
