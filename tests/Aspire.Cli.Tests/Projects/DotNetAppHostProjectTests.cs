// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Aspire.Cli.Tests.Projects;

public class DotNetAppHostProjectTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);
    private readonly List<ServiceProvider> _serviceProviders = [];

    public void Dispose()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }

        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_DefaultsToDevelopmentForRun()
    {
        var appHostFile = CreateSingleFileAppHost();
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Development", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_DefaultsToProductionForPublish()
    {
        var appHostFile = CreateSingleFileAppHost();
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFilePublishEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_EnvironmentArgumentTakesPrecedenceOverDefaultEnvironment()
    {
        var appHostFile = CreateSingleFileAppHost();
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFilePublishEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_StripsLaunchProfileEnvironmentButKeepsEndpoints()
    {
        var appHostFile = CreateSingleFileAppHost();
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "apphost.run.json"), """
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:19000;http://localhost:15000",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "DOTNET_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21000",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22000"
                  }
                }
              }
            }
            """);

        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFilePublishEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:19000;http://localhost:15000", env["ASPNETCORE_URLS"]);
        Assert.Equal("https://localhost:21000", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:22000", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_InheritedAspireEnvironmentOverridesDefaultEnvironment()
    {
        var appHostFile = CreateSingleFileAppHost();
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFilePublishEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>
            {
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
            });

        Assert.Equal("Staging", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public async Task RunAsync_SingleFileAppHostWithoutRunJsonPassesDevelopmentEnvironmentToRunner()
    {
        var appHostFile = CreateSingleFileAppHost();
        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.False(options.NoLaunchProfile);
            Assert.Equal("Development", env!["DOTNET_ENVIRONMENT"]);
            Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
            Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
            return Task.FromResult(0);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = true,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_SingleFileAppHostUsesEnvironmentArgumentWhenProvided()
    {
        var appHostFile = CreateSingleFileAppHost();
        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.False(options.NoLaunchProfile);
            Assert.Equal(["--environment", "Staging"], args);
            Assert.Equal("Staging", env!["DOTNET_ENVIRONMENT"]);
            Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
            return Task.FromResult(0);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = true,
            NoRestore = false,
            UnmatchedTokens = ["--environment", "Staging"],
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostUsingCliBundlePassesBundleEnvironmentToRunner()
    {
        var appHostFile = CreateProjectAppHost();
        var bundleRoot = CreateCliBundle(out var layout);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, properties, _, _) =>
            {
                Assert.Contains("AspireUseCliBundle", properties);
                return (0, JsonDocument.Parse("""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "AspireUseCliBundle": "true"
                      },
                      "Items": {}
                    }
                    """));
            }
        };
        var project = CreateDotNetAppHostProject(runner, layout);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.Equal(Path.Combine(bundleRoot.FullName, BundleDiscovery.DcpDirectoryName), env![BundleDiscovery.DcpPathEnvVar]);
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.DashboardPathEnvVar]);
            return Task.FromResult(0);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_SingleFileAppHostUsingCliBundlePassesBundleEnvironmentToRunner()
    {
        var appHostFile = CreateSingleFileAppHost(useCliBundle: true);
        var bundleRoot = CreateCliBundle(out var layout);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFile, _, _, _) =>
            {
                Assert.Equal(appHostFile.FullName, projectFile.FullName);
                return 0;
            },
            GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, properties, _, _) =>
            {
                Assert.Equal(appHostFile.FullName, projectFile.FullName);
                Assert.Contains("AspireUseCliBundle", properties);
                return (0, JsonDocument.Parse("""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "AspireUseCliBundle": "true"
                      },
                      "Items": {}
                    }
                    """));
            }
        };
        var project = CreateDotNetAppHostProject(runner, layout);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.False(options.NoLaunchProfile);
            Assert.Equal(Path.Combine(bundleRoot.FullName, BundleDiscovery.DcpDirectoryName), env![BundleDiscovery.DcpPathEnvVar]);
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.DashboardPathEnvVar]);
            return Task.FromResult(0);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task PublishAsync_SingleFileAppHostStripsRunProfileEnvironmentBeforeInvokingRunner()
    {
        var appHostFile = CreateSingleFileAppHost();
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "apphost.run.json"), """
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:19000;http://localhost:15000",
                  "environmentVariables": {
                    "ASPIRE_ENVIRONMENT": "Development",
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "DOTNET_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21000",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22000"
                  }
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.True(options.NoLaunchProfile);
            Assert.Equal(["--operation", "publish"], args);
            Assert.Equal("Production", env!["DOTNET_ENVIRONMENT"]);
            Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
            Assert.Equal("https://localhost:19000;http://localhost:15000", env["ASPNETCORE_URLS"]);
            Assert.Equal("https://localhost:21000", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
            Assert.Equal("https://localhost:22000", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
            Assert.False(env.ContainsKey("ASPIRE_ENVIRONMENT"));
            return Task.FromResult(0);
        };

        var exitCode = await project.PublishAsync(new PublishContext
        {
            AppHostFile = appHostFile,
            WorkingDirectory = _workspace.WorkspaceRoot,
            Arguments = ["--operation", "publish"],
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task PublishAsync_SingleFileAppHostUsesEnvironmentArgumentWhenProvided()
    {
        var appHostFile = CreateSingleFileAppHost();
        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.True(options.NoLaunchProfile);
            Assert.Equal(["--operation", "publish", "--environment", "Staging"], args);
            Assert.Equal("Staging", env!["DOTNET_ENVIRONMENT"]);
            Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
            return Task.FromResult(0);
        };

        var exitCode = await project.PublishAsync(new PublishContext
        {
            AppHostFile = appHostFile,
            WorkingDirectory = _workspace.WorkspaceRoot,
            Arguments = ["--operation", "publish", "--environment", "Staging"],
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    private FileInfo CreateSingleFileAppHost(bool useCliBundle = false)
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.cs");
        var useCliBundleProperty = useCliBundle ? "#:property AspireUseCliBundle=true" : string.Empty;
        File.WriteAllText(appHostPath, """
            #:sdk Aspire.AppHost.Sdk@13.0.0
            {0}

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """.Replace("{0}", useCliBundleProperty, StringComparison.Ordinal));

        return new FileInfo(appHostPath);
    }

    private FileInfo CreateProjectAppHost()
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        File.WriteAllText(appHostPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
                <AspireUseCliBundle>true</AspireUseCliBundle>
              </PropertyGroup>
            </Project>
            """);

        return new FileInfo(appHostPath);
    }

    private DirectoryInfo CreateCliBundle(out LayoutConfiguration layout)
    {
        var bundleRoot = Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, Guid.NewGuid().ToString()));
        Directory.CreateDirectory(Path.Combine(bundleRoot.FullName, BundleDiscovery.DcpDirectoryName));
        Directory.CreateDirectory(Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName));

        layout = new LayoutConfiguration
        {
            LayoutPath = bundleRoot.FullName,
            Components = new LayoutComponents
            {
                Dcp = BundleDiscovery.DcpDirectoryName,
                Managed = BundleDiscovery.ManagedDirectoryName,
            }
        };

        return bundleRoot;
    }

    private DotNetAppHostProject CreateDotNetAppHostProject(TestDotNetCliRunner runner, LayoutConfiguration? layout = null)
    {
        var services = CliTestHelper.CreateServiceCollection(_workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => runner;
            if (layout is not null)
            {
                options.BundleServiceFactory = _ => new TestBundleService(isBundle: true)
                {
                    Layout = layout
                };
            }
        });

        var provider = services.BuildServiceProvider();
        _serviceProviders.Add(provider);
        return provider.GetRequiredService<DotNetAppHostProject>();
    }
}
