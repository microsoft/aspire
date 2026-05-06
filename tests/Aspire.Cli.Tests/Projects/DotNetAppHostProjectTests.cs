// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public void ConfigureSingleFileRunEnvironment_AppliesProfileFromAspireConfigJson()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" },
              "profiles": {
                "https": {
                  "applicationUrl": "https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "DOTNET_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://myapp.dev.localhost:21050",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://myapp.dev.localhost:22050"
                  }
                }
              }
            }
            """);
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050", env["ASPNETCORE_URLS"]);
        Assert.Equal("https://myapp.dev.localhost:21050", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://myapp.dev.localhost:22050", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        // Run path copies profile env vars verbatim (matches what dotnet does when reading apphost.run.json natively).
        Assert.Equal("Development", env["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Development", env["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_AppliesProfileFromAspireConfigJson()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" },
              "profiles": {
                "https": {
                  "applicationUrl": "https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "DOTNET_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://myapp.dev.localhost:21050",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://myapp.dev.localhost:22050"
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

        Assert.Equal("https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050", env["ASPNETCORE_URLS"]);
        Assert.Equal("https://myapp.dev.localhost:21050", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://myapp.dev.localhost:22050", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        // Publish path filters env-name vars from profile, then ApplyEffectiveEnvironment sets DOTNET_ENVIRONMENT=Production.
        Assert.Equal("Production", env["DOTNET_ENVIRONMENT"]);
        Assert.False(env.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public void ConfigureSingleFilePublishEnvironment_AppHostRunJsonWinsOverAspireConfigJson()
    {
        var appHostFile = CreateSingleFileAppHost();
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "apphost.run.json"), """
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://from-run-json:19000",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://from-run-json:21000"
                  }
                }
              }
            }
            """);
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" },
              "profiles": {
                "https": {
                  "applicationUrl": "https://from-config-json:17050",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://from-config-json:21050"
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

        Assert.Equal("https://from-run-json:19000", env["ASPNETCORE_URLS"]);
        Assert.Equal("https://from-run-json:21000", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_FallsBackToDefaultsWhenAspireConfigJsonHasNoProfiles()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" }
            }
            """);
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
        Assert.Equal("Development", env["DOTNET_ENVIRONMENT"]);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_FallsBackToDefaultsWhenProfileLacksApplicationUrl()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" },
              "profiles": {
                "https": {
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://shouldnotapply:21050"
                  }
                }
              }
            }
            """);
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
        Assert.Equal("https://localhost:21293", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_SkipsAspireConfigWhenAppHostPathMismatches()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "other-apphost.cs" },
              "profiles": {
                "https": {
                  "applicationUrl": "https://shouldnotapply:17050"
                }
              }
            }
            """);
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_FallsBackToDefaultsWhenAspireConfigJsonIsMalformed()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, "{ this is not valid json");
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("https://localhost:17193;http://localhost:15069", env["ASPNETCORE_URLS"]);
        Assert.Equal("Development", env["DOTNET_ENVIRONMENT"]);
    }

    [Fact]
    public void ConfigureSingleFileRunEnvironment_EnvironmentArgumentOverridesProfileDotNetEnvironment()
    {
        var appHostFile = CreateSingleFileAppHost();
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "appHost": { "path": "apphost.cs" },
              "profiles": {
                "https": {
                  "applicationUrl": "https://myapp.dev.localhost:17050",
                  "environmentVariables": {
                    "DOTNET_ENVIRONMENT": "Development"
                  }
                }
              }
            }
            """);
        var env = new Dictionary<string, string>();

        DotNetAppHostProject.ConfigureSingleFileRunEnvironment(
            appHostFile,
            env,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", env["DOTNET_ENVIRONMENT"]);
        Assert.Equal("https://myapp.dev.localhost:17050", env["ASPNETCORE_URLS"]);
    }

    private FileInfo CreateSingleFileAppHost()
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.cs");
        File.WriteAllText(appHostPath, """
            #:sdk Aspire.AppHost.Sdk@13.0.0

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        return new FileInfo(appHostPath);
    }

    private DotNetAppHostProject CreateDotNetAppHostProject(TestDotNetCliRunner runner)
    {
        var services = CliTestHelper.CreateServiceCollection(_workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => runner;
        });

        var provider = services.BuildServiceProvider();
        _serviceProviders.Add(provider);
        return provider.GetRequiredService<DotNetAppHostProject>();
    }

    private static void WriteAspireConfigJson(string directory, string content)
        => File.WriteAllText(Path.Combine(directory, "aspire.config.json"), content);
}
