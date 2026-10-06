// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
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
    public async Task GetPackageVersionsAsync_ExpandsVersionsOfExactIdMatch()
    {
        string? observedQuery = null;
        var observedTake = -1;

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (query, _, take, _, _, _, _) =>
                    {
                        observedQuery = query;
                        observedTake = take;

                        // An ordinary search also returns packages whose IDs only start with the query.
                        return Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                        [
                            new("Aspire.Hosting.Redis", "13.3.0", "nuget.org", ["13.3.0", "13.2.0"]),
                            new("Aspire.Hosting.Redis.Extras", "14.0.0", "nuget.org", ["14.0.0"])
                        ]);
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting.Redis",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None);

        Assert.Equal("Aspire.Hosting.Redis", observedQuery);
        Assert.Equal(1000, observedTake);
        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.3.0", package.Version);
                Assert.Equal("nuget.org", package.Source);
            },
            package =>
            {
                Assert.Equal("Aspire.Hosting.Redis", package.Id);
                Assert.Equal("13.2.0", package.Version);
                Assert.Equal("nuget.org", package.Source);
            });
    }

    [Fact]
    public async Task GetPackageVersionsAsync_MatchesPackageIdCaseSensitively()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                    [
                        new("aspire.hosting.redis", "13.3.0", "nuget.org", ["13.3.0"])
                    ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var packages = await nuGetPackageCache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting.Redis",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task GetPackageVersionsAsync_IncludesDeprecatedPackage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _) =>
                        Task.FromResult<IReadOnlyList<NuGetSearchResult>>(
                        [
                            new("Aspire.Hosting.Dapr", "13.4.0", "nuget.org", ["13.4.0"])
                        ])
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var package = Assert.Single(await nuGetPackageCache.GetPackageVersionsAsync(
            workspace.WorkspaceRoot,
            "Aspire.Hosting.Dapr",
            prerelease: false,
            nugetConfigFile: null,
            useCache: true,
            CancellationToken.None));

        Assert.Equal("Aspire.Hosting.Dapr", package.Id);
    }

    [Fact]
    public async Task SearchFailureReportsHelperExitCodeMessage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var expectedException = new NuGetOperationException("Error: Search failed." + Environment.NewLine);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, _) => throw expectedException
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        var exception = await Assert.ThrowsAsync<NuGetPackageCacheException>(() =>
            nuGetPackageCache.GetPackageVersionsAsync(
                workspace.WorkspaceRoot,
                "Aspire.Hosting.Redis",
                prerelease: false,
                nugetConfigFile: null,
                useCache: true,
                CancellationToken.None));

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, ErrorStrings.FailedToSearchForPackages, 1),
            exception.Message);
        Assert.Same(expectedException, exception.InnerException);
    }

    [Fact]
    public async Task SearchCancellationIsNotWrapped()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.NuGetClientFactory = _ =>
            {
                return new FakeNuGetClient
                {
                    SearchCallback = (_, _, _, _, _, _, cancellationToken) =>
                        Task.FromCanceled<IReadOnlyList<NuGetSearchResult>>(cancellationToken)
                };
            };
        });

        using var provider = services.BuildServiceProvider();

        var nuGetPackageCache = CreateCache(provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            nuGetPackageCache.GetPackageVersionsAsync(
                workspace.WorkspaceRoot,
                "Aspire.Hosting.Redis",
                prerelease: false,
                nugetConfigFile: null,
                useCache: true,
                cancellationSource.Token));
    }

    private static INuGetPackageCache CreateCache(IServiceProvider provider) =>
        new BundleNuGetPackageCache(
            provider.GetRequiredService<INuGetClient>(),
            NullLogger<BundleNuGetPackageCache>.Instance,
            provider.GetRequiredService<IFeatures>());
}
