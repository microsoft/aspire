// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Aspire.Cli.Tests.Projects;

public class DotNetAppHostProjectTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);
    private readonly List<ServiceProvider> _serviceProviders = [];

    public DotNetAppHostProjectTests UseFakeRepoRoot()
    {
        // Tests that build their own fake bundle layout under a temp directory must opt out
        // of the in-repo aspire-managed discovery; otherwise the repo's real built artifact
        // shadows the fake bundle path the test pre-stamped into the layout.
        DotNetAppHostProject.RepoLocalManagedPathProviderOverride = () => null;
        return this;
    }

    public void Dispose()
    {
        DotNetAppHostProject.RepoLocalManagedPathProviderOverride = null;

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
    public async Task FindAndStopRunningInstanceAsync_CleansUpDeadPidSocketAndReturnsNoRunningInstance()
    {
        var appHostFile = CreateSingleFileAppHost();
        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);
        var socketPath = CreateMatchingSocketFile(appHostFile.FullName, int.MaxValue - 1);

        var result = await project.FindAndStopRunningInstanceAsync(
            appHostFile,
            _workspace.WorkspaceRoot,
            CancellationToken.None);

        Assert.Equal(RunningInstanceResult.NoRunningInstance, result);
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public async Task FindAndStopRunningInstanceAsync_KeepsLivePidSocketAndReportsStopFailureWhenConnectionFails()
    {
        var appHostFile = CreateSingleFileAppHost();
        var runner = new TestDotNetCliRunner();
        var project = CreateDotNetAppHostProject(runner);
        var socketPath = CreateMatchingSocketFile(appHostFile.FullName, Environment.ProcessId);

        var result = await project.FindAndStopRunningInstanceAsync(
            appHostFile,
            _workspace.WorkspaceRoot,
            CancellationToken.None);

        Assert.Equal(RunningInstanceResult.StopFailed, result);
        Assert.True(File.Exists(socketPath));
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostUsingCliBundlePassesBundleEnvironmentToRunner()
    {
        UseFakeRepoRoot();
        var appHostFile = CreateProjectAppHost();
        var bundleRoot = CreateCliBundle(out var layout);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, properties, _, _) =>
            {
                Assert.Contains("AspireUseCliBundle", properties);
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
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
            Assert.Equal(bundleRoot.FullName, env!["AspireCliBundlePath"]);
            Assert.Equal(Path.Combine(bundleRoot.FullName, BundleDiscovery.DcpDirectoryName), env![BundleDiscovery.DcpPathEnvVar]);
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.DashboardPathEnvVar]);
            // Terminal host env vars are always injected when the bundle layout is available
            // — see the comment in ConfigureCliBundleEnvironmentAsync. For CliBundle AppHosts
            // they sit alongside the DCP/Dashboard vars; both point at aspire-managed.
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.TerminalHostPathEnvVar]);
            Assert.Equal("terminalhost", env[BundleDiscovery.TerminalHostInvocationArgsEnvVar]);
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
    public async Task RunAsync_ProjectAppHostNotUsingCliBundleStillReceivesTerminalHostEnvironment()
    {
        UseFakeRepoRoot();
        // AppHosts created by `aspire new` default to per-RID NuGets (AspireUseCliBundle != true).
        // Today no per-RID NuGet stamps the terminal host metadata path, so without env-var
        // injection WithTerminal() resources fail at run time with <unresolved>. The CLI ships
        // aspire-managed in its bundle and that binary exposes the `terminalhost` subcommand,
        // so injecting ASPIRE_TERMINAL_HOST_PATH unconditionally lights up WithTerminal() for
        // per-RID-NuGet AppHosts launched via `aspire run`.
        var appHostFile = CreateProjectAppHost();
        var bundleRoot = CreateCliBundle(out var layout);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "AspireUseCliBundle": "false"
                      },
                      "Items": {}
                    }
                    """));
            }
        };
        var project = CreateDotNetAppHostProject(runner, layout);

        runner.RunAsyncCallback = (projectFile, _, _, _, _, env, _, _, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);

            // DCP/Dashboard env vars must NOT be injected for non-CliBundle AppHosts —
            // they would clobber the per-RID NuGet metadata path the AppHost was built against.
            Assert.False(env!.ContainsKey(BundleDiscovery.DcpPathEnvVar));
            Assert.False(env.ContainsKey(BundleDiscovery.DashboardPathEnvVar));

            // Terminal host env vars must be injected even though AspireUseCliBundle=false.
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.TerminalHostPathEnvVar]);
            Assert.Equal("terminalhost", env[BundleDiscovery.TerminalHostInvocationArgsEnvVar]);
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
    public async Task RunAsync_ProjectAppHostNotUsingCliBundleUsesRepoLocalManagedWhenAvailable()
    {
        // When running `dotnet run --project src/Aspire.Cli` from inside the Aspire repo,
        // the just-built aspire-managed under artifacts/ should be preferred over the bundle
        // layout aspire-managed. The bundle layout points at the user's installed CLI cache
        // (e.g. ~/.aspire/bundle/) whose aspire-managed predates the `terminalhost`
        // subcommand and fails the AppHost launch.
        var appHostFile = CreateProjectAppHost();
        var bundleRoot = CreateCliBundle(out var layout);
        var repoLocalManaged = Path.Combine(_workspace.WorkspaceRoot.FullName, "repo-local-aspire-managed");
        File.WriteAllText(repoLocalManaged, "fake");
        DotNetAppHostProject.RepoLocalManagedPathProviderOverride = () => repoLocalManaged;

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "AspireUseCliBundle": "false"
                      },
                      "Items": {}
                    }
                    """));
            }
        };
        var project = CreateDotNetAppHostProject(runner, layout);

        runner.RunAsyncCallback = (_, _, _, _, _, env, _, _, _) =>
        {
            // Repo-local managed path wins over the bundle layout path.
            Assert.Equal(repoLocalManaged, env![BundleDiscovery.TerminalHostPathEnvVar]);
            Assert.NotEqual(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.TerminalHostPathEnvVar]);
            // Args still synthesized — repo-local aspire-managed is the same dispatcher binary.
            Assert.Equal("terminalhost", env[BundleDiscovery.TerminalHostInvocationArgsEnvVar]);
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
    public async Task RunAsync_PreservesExplicitTerminalHostEnvironmentVariables()
    {
        UseFakeRepoRoot();
        // Users can side-load a custom terminal host binary by setting ASPIRE_TERMINAL_HOST_PATH
        // themselves. The CLI must not overwrite either the path OR the invocation args in that
        // case — a custom binary may not understand the "terminalhost" dispatcher arg that
        // aspire-managed uses, so the path/args must be preserved together as a pair.
        var appHostFile = CreateProjectAppHost();
        _ = CreateCliBundle(out var layout);
        var customTerminalHost = Path.Combine(_workspace.WorkspaceRoot.FullName, "my-custom-terminal-host");

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "AspireUseCliBundle": "false"
                      },
                      "Items": {}
                    }
                    """));
            }
        };
        var project = CreateDotNetAppHostProject(runner, layout);

        runner.RunAsyncCallback = (_, _, _, _, _, env, _, _, _) =>
        {
            // User-provided path is preserved verbatim.
            Assert.Equal(customTerminalHost, env![BundleDiscovery.TerminalHostPathEnvVar]);
            // And the CLI must NOT synthesize invocation args for a binary it didn't choose —
            // those args are bundle-binary-specific (today: "terminalhost" for aspire-managed).
            Assert.False(env.ContainsKey(BundleDiscovery.TerminalHostInvocationArgsEnvVar));
            return Task.FromResult(0);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>
            {
                [BundleDiscovery.TerminalHostPathEnvVar] = customTerminalHost
            }
        }, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostUsesDirectCommandLaunchAndAppliesLaunchSettings()
    {
        var appHostFile = CreateProjectAppHost();
        var targetPath = CreateBuiltAppHostAssembly("AppHost.dll");
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var runWorkingDirectory = Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, "run-cwd"));
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        var targetPathJson = JsonSerializer.Serialize(targetPath.FullName);
        var runWorkingDirectoryJson = JsonSerializer.Serialize(runWorkingDirectory.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), """
            {
              "profiles": {
                "IIS Express": {
                  "commandName": "IISExpress",
                  "applicationUrl": "https://should-not-be-used"
                },
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:15000",
                  "commandLineArgs": "--from-profile \"profile value\"",
                  "environmentVariables": {
                    "DOTNET_ENVIRONMENT": "Development",
                    "CUSTOM_ENV": "custom-value"
                  }
                },
                "https": {
                  "commandName": "Project",
                  "applicationUrl": "https://should-not-win"
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetPath": {{targetPathJson}},
                        "RunWorkingDirectory": {{runWorkingDirectoryJson}},
                        "RunArguments": "--from-msbuild \"two words\"",
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (projectFile, command, workingDirectory, args, env, _, options, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.Equal(appHostCommand.FullName, command);
            Assert.Equal(runWorkingDirectory.FullName, workingDirectory.FullName);
            Assert.False(options.NoLaunchProfile);
            Assert.Equal(
                ["--from-msbuild", "two words", "--explicit", "1"],
                args);
            Assert.NotNull(env);
            Assert.Equal("http", env["DOTNET_LAUNCH_PROFILE"]);
            Assert.Equal("http://localhost:15000", env["ASPNETCORE_URLS"]);
            Assert.Equal("Development", env["DOTNET_ENVIRONMENT"]);
            Assert.Equal("context-value", env["CUSTOM_ENV"]);
            return Task.FromResult(123);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            UnmatchedTokens = ["--explicit", "1"],
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["CUSTOM_ENV"] = "context-value"
            }
        }, CancellationToken.None);

        Assert.Equal(123, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchCanBeDisabledByConfig()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        WriteAspireConfigJson(appHostFile.DirectoryName!, """
            {
              "dotnetAppHostDirectLaunchDisabled": "true"
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAppHostCommandAsyncCallback = (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("direct AppHost launch should not be used when disabled.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, _, _, _, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.Equal(["--explicit", "1"], args);
            return Task.FromResult(77);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            UnmatchedTokens = ["--explicit", "1"],
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackWhenDotnetExecTargetIsMissing()
    {
        var missingTargetPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "bin", "missing", "AppHost.dll");

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: "dotnet", runArguments: $"exec \"{missingTargetPath}\""));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackWhenRuntimeConfigIsMissing()
    {
        var outputDirectory = Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, "bin", Guid.NewGuid().ToString("N")));
        var targetPath = Path.Combine(outputDirectory.FullName, "AppHost.dll");
        File.WriteAllText(targetPath, string.Empty);

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: "dotnet", runArguments: $"exec \"{targetPath}\""));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackWhenNativeRunCommandIsMissing()
    {
        var missingRunCommand = Path.Combine(_workspace.WorkspaceRoot.FullName, "bin", "missing", "AppHost");

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: missingRunCommand));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackForMultiTargetedAppHost()
    {
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: appHostCommand.FullName, targetFrameworks: "net10.0;net9.0"));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackWhenRunCommandIsMissing()
    {
        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: "   "));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchFallsBackWhenDotnetRunArgumentsDoNotUseExec()
    {
        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: "dotnet", runArguments: "run --project AppHost.csproj"));

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostUsesSdkPathWhenWatchIsEnabled()
    {
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: appHostCommand.FullName),
            expectedWatch: true,
            configureServices: options => options.EnabledFeatures = [KnownFeatures.DefaultWatchEnabled]);

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostUsesSdkPathForExtensionHost()
    {
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");

        var exitCode = await AssertProjectAppHostFallsBackToDotNetRunAsync(
            () => CreateAppHostInfoJson(runCommand: appHostCommand.FullName),
            configureServices: options =>
            {
                options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
                options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
            });

        Assert.Equal(77, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchUsesProfileArgsWhenRunCommandHasNoArgs()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "commandLineArgs": "--from-profile \"profile value\""
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (_, command, _, args, env, _, _, _) =>
        {
            Assert.Equal(appHostCommand.FullName, command);
            Assert.Equal(["--from-profile", "profile value"], args);
            Assert.NotNull(env);
            Assert.Equal("http", env["DOTNET_LAUNCH_PROFILE"]);
            return Task.FromResult(88);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(88, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchExpandsLaunchSettingsEnvironmentVariablesAndArgs()
    {
        var variableName = $"ASPIRE_TEST_EXPAND_{Guid.NewGuid():N}";
        var variableReference = $"%{variableName}%";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, "expanded-value");

        try
        {
            var appHostFile = CreateProjectAppHost();
            var appHostCommand = CreateBuiltAppHostCommand("AppHost");
            Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
            File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), $$"""
                {
                  "profiles": {
                    "http": {
                      "commandName": "Project",
                      "commandLineArgs": "--expanded {{variableReference}}",
                      "environmentVariables": {
                        "CUSTOM_ENV": "{{variableReference}}/child"
                      }
                    }
                  }
                }
                """);

            var runner = new TestDotNetCliRunner
            {
                BuildAsyncCallback = (_, _, _, _) => 0,
                GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => (0, CreateAppHostInfoJson(runCommand: appHostCommand.FullName)),
                RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
            };
            var project = CreateDotNetAppHostProject(runner);

            runner.RunAppHostCommandAsyncCallback = (_, _, _, args, env, _, _, _) =>
            {
                Assert.Equal(["--expanded", "expanded-value"], args);
                Assert.NotNull(env);
                Assert.Equal("expanded-value/child", env["CUSTOM_ENV"]);
                return Task.FromResult(88);
            };

            var exitCode = await project.RunAsync(new AppHostProjectContext
            {
                AppHostFile = appHostFile,
                NoBuild = false,
                NoRestore = false,
                WorkingDirectory = _workspace.WorkspaceRoot,
                EnvironmentVariables = new Dictionary<string, string>()
            }, CancellationToken.None);

            Assert.Equal(88, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchPreservesDotnetExecRunArguments()
    {
        var appHostFile = CreateProjectAppHost();
        var targetPath = CreateBuiltAppHostAssembly("App Host.dll");
        var escapedTargetPath = targetPath.FullName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": "dotnet.exe",
                        "RunArguments": "exec \"{{escapedTargetPath}}\" --from-msbuild",
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (_, command, _, args, _, _, _, _) =>
        {
            Assert.Equal("dotnet.exe", command);
            Assert.Equal(["exec", targetPath.FullName, "--from-msbuild", "--explicit", "1"], args);
            return Task.FromResult(99);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            UnmatchedTokens = ["--explicit", "1"],
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(99, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchReadsFlatRunJson()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "AppHost.run.json"), """
            {
              "profiles": {
                "flat": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:16000",
                  "environmentVariables": {
                    "CUSTOM_ENV": "from-run-json"
                  }
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (_, command, _, args, env, _, _, _) =>
        {
            Assert.Equal(appHostCommand.FullName, command);
            Assert.Empty(args);
            Assert.NotNull(env);
            Assert.Equal("flat", env["DOTNET_LAUNCH_PROFILE"]);
            Assert.Equal("http://localhost:16000", env["ASPNETCORE_URLS"]);
            Assert.Equal("from-run-json", env["CUSTOM_ENV"]);
            return Task.FromResult(101);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(101, exitCode);
    }

    [Fact]
    public async Task RunAsync_VbProjectAppHostDirectLaunchReadsMyProjectLaunchSettings()
    {
        var appHostFile = CreateProjectAppHost("AppHost.vbproj");
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "My Project"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "My Project", "launchSettings.json"), """
            {
              "profiles": {
                "vb": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:17000"
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when the built target is known.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (_, command, _, _, env, _, _, _) =>
        {
            Assert.Equal(appHostCommand.FullName, command);
            Assert.NotNull(env);
            Assert.Equal("vb", env["DOTNET_LAUNCH_PROFILE"]);
            Assert.Equal("http://localhost:17000", env["ASPNETCORE_URLS"]);
            return Task.FromResult(102);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(102, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostFallsBackToDotnetRunForExecutableLaunchProfile()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), """
            {
              "profiles": {
                "tool": {
                  "commandName": "Executable",
                  "executablePath": "custom-tool"
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAppHostCommandAsyncCallback = (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("direct AppHost launch should not be used for executable launch profiles.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, _, _, _, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.Equal(["--explicit", "1"], args);
            return Task.FromResult(103);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            UnmatchedTokens = ["--explicit", "1"],
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(103, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostFallsBackToDotnetRunWhenLaunchSettingsProfilesIsNull()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), """
            {
              "profiles": null
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAppHostCommandAsyncCallback = (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("direct AppHost launch should not be used when launch settings do not contain a usable profile.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, _, _, _, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.False(watch);
            Assert.True(noBuild);
            Assert.False(noRestore);
            Assert.Empty(args);
            return Task.FromResult(104);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(104, exitCode);
    }

    [Fact]
    public async Task RunAsync_ProjectAppHostDirectLaunchSkipsNullLaunchSettingsProfileAndEnvironmentValues()
    {
        var appHostFile = CreateProjectAppHost();
        var appHostCommand = CreateBuiltAppHostCommand("AppHost");
        var appHostCommandJson = JsonSerializer.Serialize(appHostCommand.FullName);
        Directory.CreateDirectory(Path.Combine(appHostFile.DirectoryName!, "Properties"));
        File.WriteAllText(Path.Combine(appHostFile.DirectoryName!, "Properties", "launchSettings.json"), """
            {
              "profiles": {
                "null-profile": null,
                "http": {
                  "commandName": "Project",
                  "environmentVariables": {
                    "NULL_ENV": null,
                    "CUSTOM_ENV": "custom-value"
                  }
                }
              }
            }
            """);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) =>
            {
                return (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "MSBuildVersion": "17.0.0",
                        "IsAspireHost": "true",
                        "AspireHostingSDKVersion": "{{VersionHelper.GetDefaultTemplateVersion()}}",
                        "RunCommand": {{appHostCommandJson}},
                        "TargetFramework": "net10.0"
                      },
                      "Items": {}
                    }
                    """));
            },
            RunAsyncCallback = (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("dotnet run should not be used when a later launch profile is usable.")
        };
        var project = CreateDotNetAppHostProject(runner);

        runner.RunAppHostCommandAsyncCallback = (_, command, _, _, env, _, _, _) =>
        {
            Assert.Equal(appHostCommand.FullName, command);
            Assert.NotNull(env);
            Assert.Equal("http", env["DOTNET_LAUNCH_PROFILE"]);
            Assert.Equal("custom-value", env["CUSTOM_ENV"]);
            Assert.False(env.ContainsKey("NULL_ENV"));
            return Task.FromResult(105);
        };

        var exitCode = await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);

        Assert.Equal(105, exitCode);
    }

    [Fact]
    public async Task RunAsync_SingleFileAppHostUsingCliBundlePassesBundleEnvironmentToRunner()
    {
        UseFakeRepoRoot();
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
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.TerminalHostPathEnvVar]);
            Assert.Equal("terminalhost", env[BundleDiscovery.TerminalHostInvocationArgsEnvVar]);
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

    [Fact]
    public async Task PublishAsync_SingleFileAppHostUsingCliBundlePassesBundleEnvironmentToRunner()
    {
        var appHostFile = CreateSingleFileAppHost(useCliBundle: true);
        var bundleRoot = CreateCliBundle(out var layout);

        var runner = new TestDotNetCliRunner
        {
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
            Assert.True(options.NoLaunchProfile);
            Assert.Equal(["--operation", "publish"], args);
            Assert.Equal("Production", env!["DOTNET_ENVIRONMENT"]);
            Assert.Equal(Path.Combine(bundleRoot.FullName, BundleDiscovery.DcpDirectoryName), env[BundleDiscovery.DcpPathEnvVar]);
            Assert.Equal(
                Path.Combine(bundleRoot.FullName, BundleDiscovery.ManagedDirectoryName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                env[BundleDiscovery.DashboardPathEnvVar]);
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

    private string CreateMatchingSocketFile(string appHostPath, int pid)
    {
        var backchannelsDir = Path.Combine(_workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var resolvedAppHostPath = PathNormalizer.ResolveSymlinks(appHostPath);
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(resolvedAppHostPath, _workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);
        var socketPath = Path.Combine(
            backchannelsDir,
            $"{appHostId}a1b2C3d4.{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        File.WriteAllText(socketPath, "");
        return socketPath;
    }

    private FileInfo CreateBuiltAppHostAssembly(string fileName)
    {
        var outputDirectory = Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, "bin", Guid.NewGuid().ToString("N")));
        var targetPath = Path.Combine(outputDirectory.FullName, fileName);
        File.WriteAllText(targetPath, string.Empty);
        File.WriteAllText(Path.ChangeExtension(targetPath, ".runtimeconfig.json"), "{}");
        return new FileInfo(targetPath);
    }

    private FileInfo CreateBuiltAppHostCommand(string fileName)
    {
        var outputDirectory = Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, "bin", Guid.NewGuid().ToString("N")));
        var commandPath = Path.Combine(outputDirectory.FullName, fileName);
        File.WriteAllText(commandPath, string.Empty);
        return new FileInfo(commandPath);
    }

    private async Task<int> AssertProjectAppHostFallsBackToDotNetRunAsync(
        Func<JsonDocument> createAppHostInfo,
        bool expectedWatch = false,
        Action<CliServiceCollectionTestOptions>? configureServices = null)
    {
        var appHostFile = CreateProjectAppHost();
        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) => 0,
            GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => (0, createAppHostInfo()),
            RunAppHostCommandAsyncCallback = (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("direct AppHost launch should not be used when the run metadata is not directly launchable.")
        };
        var project = CreateDotNetAppHostProject(runner, configureServices: configureServices);

        runner.RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, _, _, _, _) =>
        {
            Assert.Equal(appHostFile.FullName, projectFile.FullName);
            Assert.Equal(expectedWatch, watch);
            Assert.Equal(!expectedWatch, noBuild);
            Assert.False(noRestore);
            Assert.Empty(args);
            return Task.FromResult(77);
        };

        return await project.RunAsync(new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            NoBuild = false,
            NoRestore = false,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        }, CancellationToken.None);
    }

    private static JsonDocument CreateAppHostInfoJson(
        string? runCommand,
        string? targetPath = null,
        string? runWorkingDirectory = null,
        string? runArguments = null,
        string? targetFrameworks = null)
    {
        var properties = new Dictionary<string, string?>
        {
            ["MSBuildVersion"] = "17.0.0",
            ["IsAspireHost"] = "true",
            ["AspireHostingSDKVersion"] = VersionHelper.GetDefaultTemplateVersion(),
            ["RunCommand"] = runCommand,
            ["TargetPath"] = targetPath,
            ["RunWorkingDirectory"] = runWorkingDirectory,
            ["RunArguments"] = runArguments,
            ["TargetFramework"] = "net10.0",
            ["TargetFrameworks"] = targetFrameworks
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(new { Properties = properties, Items = new { } }));
    }

    private FileInfo CreateProjectAppHost(string fileName = "AppHost.csproj")
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, fileName);
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

    private DotNetAppHostProject CreateDotNetAppHostProject(
        TestDotNetCliRunner runner,
        LayoutConfiguration? layout = null,
        Action<CliServiceCollectionTestOptions>? configureServices = null)
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

            configureServices?.Invoke(options);
        });

        var provider = services.BuildServiceProvider();
        _serviceProviders.Add(provider);
        return provider.GetRequiredService<DotNetAppHostProject>();
    }

    private static void WriteAspireConfigJson(string directory, string content)
        => File.WriteAllText(Path.Combine(directory, "aspire.config.json"), content);
}
