// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetPackageCacheTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NonAspireCliPackagesWillNotBeConsidered()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _) =>
                {
                    // Simulate a search that returns packages that do not match Aspire.Cli
                    return (0, [
                        new NuGetPackage { Id = "CommunityToolkit.Aspire.Hosting.Foo", Version = "9.4.0-xyz", Source = "nuget.org" },
                        new NuGetPackage { Id = "Aspire.Cli", Version = "9.4.0-preview", Source = "nuget.org" }
                    ]);
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var nuGetPackageCache = provider.GetRequiredService<INuGetPackageCache>();
        var packages = await nuGetPackageCache.GetCliPackagesAsync(workspace.WorkspaceRoot, prerelease: true, nugetConfigFile: null, CancellationToken.None).DefaultTimeout();

        Assert.Collection(
            packages,
            package => Assert.Equal("Aspire.Cli", package.Id)
        );
    }

    [Fact]
    public async Task DeprecatedPackagesAreFilteredByDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _) =>
                {
                    // Simulate a search that returns both regular and deprecated packages
                    return (0, [
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.4.0", Source = "nuget.org" },
                        new NuGetPackage { Id = "Aspire.Hosting.Dapr", Version = "9.4.0", Source = "nuget.org" }, // Deprecated
                        new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Version = "9.4.0", Source = "nuget.org" }
                    ]);
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var nuGetPackageCache = provider.GetRequiredService<INuGetPackageCache>();
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should include regular packages but exclude deprecated Dapr package
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.Contains("Aspire.Hosting.PostgreSQL", packageIds);
        Assert.DoesNotContain("Aspire.Hosting.Dapr", packageIds);
    }

    [Fact]
    public async Task DeprecatedPackagesAreIncludedWhenShowDeprecatedPackagesEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            // Enable showing deprecated packages
            configure.EnabledFeatures = [Aspire.Cli.KnownFeatures.ShowDeprecatedPackages];
            
            configure.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _) =>
                {
                    // Simulate a search that returns both regular and deprecated packages
                    return (0, [
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.4.0", Source = "nuget.org" },
                        new NuGetPackage { Id = "Aspire.Hosting.Dapr", Version = "9.4.0", Source = "nuget.org" }, // Deprecated
                        new NuGetPackage { Id = "Aspire.Hosting.PostgreSQL", Version = "9.4.0", Source = "nuget.org" }
                    ]);
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var nuGetPackageCache = provider.GetRequiredService<INuGetPackageCache>();
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should include all packages including deprecated Dapr package when showing deprecated is enabled
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.Contains("Aspire.Hosting.PostgreSQL", packageIds);
        Assert.Contains("Aspire.Hosting.Dapr", packageIds);
    }

    [Fact]
    public async Task CustomFilterBypassesDeprecatedPackageFiltering()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _) =>
                {
                    // Simulate a search that returns both regular and deprecated packages
                    return (0, [
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.4.0", Source = "nuget.org" },
                        new NuGetPackage { Id = "Aspire.Hosting.Dapr", Version = "9.4.0", Source = "nuget.org" }, // Deprecated
                        new NuGetPackage { Id = "Other.Package", Version = "9.4.0", Source = "nuget.org" }
                    ]);
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var nuGetPackageCache = provider.GetRequiredService<INuGetPackageCache>();
        
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure =>
        {
            configure.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _) =>
                {
                    // Test different casing of deprecated package name
                    return (0, [
                        new NuGetPackage { Id = "aspire.hosting.dapr", Version = "9.4.0", Source = "nuget.org" }, // lowercase
                        new NuGetPackage { Id = "ASPIRE.HOSTING.DAPR", Version = "9.4.0", Source = "nuget.org" }, // uppercase
                        new NuGetPackage { Id = "Aspire.Hosting.Redis", Version = "9.4.0", Source = "nuget.org" }
                    ]);
                };

                return runner;
            };
        });

        var provider = services.BuildServiceProvider();

        var nuGetPackageCache = provider.GetRequiredService<INuGetPackageCache>();
        var packages = await nuGetPackageCache.GetPackagesAsync(workspace.WorkspaceRoot, "Aspire.Hosting", null, prerelease: false, nugetConfigFile: null, useCache: true, CancellationToken.None).DefaultTimeout();

        // Should filter out all case variations of deprecated package
        var packageIds = packages.Select(p => p.Id).ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.DoesNotContain("aspire.hosting.dapr", packageIds);
        Assert.DoesNotContain("ASPIRE.HOSTING.DAPR", packageIds);
    }
}
