// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldKillEntireProcessTreeOnCancel_KillsOnlyTargetProcessOnWindows(bool isWindows, bool expected)
    {
        var result = DotNetAppHostProject.ShouldKillEntireProcessTreeOnCancel(isWindows);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ValidateAppHostAsync_OrdinaryMarkerFreeProject_SkipsEvaluation()
    {
        // An ordinary library project with no AppHost markers is rejected by the cheap heuristic
        // before the expensive MSBuild evaluation runs, so the resolver is never consulted.
        var ordinaryProject = CreateOrdinaryProject("Library.csproj");
        var runner = new TestDotNetCliRunner();
        var resolver = new TestAppHostInfoResolver();
        var project = CreateDotNetAppHostProject(runner, appHostInfoResolver: resolver);

        var validation = await project.ValidateAppHostAsync(ordinaryProject, CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.False(validation.IsPossiblyUnbuildable);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task ValidateAppHostAsync_AppHostConventionalProject_EvaluatesViaResolver()
    {
        // An AppHost-conventional project is not confidently rejected, so it flows into MSBuild
        // evaluation via the resolver, which confirms it is an Aspire host.
        var appHostFile = CreateProjectAppHost();
        var runner = new TestDotNetCliRunner();
        var resolver = new TestAppHostInfoResolver
        {
            GetAppHostInfoAsyncCallback = (_, _) => Task.FromResult(new AppHostProjectInfo(
                ExitCode: 0,
                IsAspireHost: true,
                AspireHostingVersion: "13.0.0",
                IsUsingCliBundle: false,
                UserSecretsId: null,
                RunCommand: null,
                TargetPath: null,
                RunWorkingDirectory: null,
                RunArguments: null,
                TargetFramework: "net10.0",
                TargetFrameworks: null))
        };
        var project = CreateDotNetAppHostProject(runner, appHostInfoResolver: resolver);

        var validation = await project.ValidateAppHostAsync(appHostFile, CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Equal("13.0.0", validation.AspireHostingVersion);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task ValidateAppHostAsync_EvaluatesCleanlyButNotAspireHost_RejectsWithoutPossiblyUnbuildable()
    {
        // A project can pass the cheap name heuristic without being an Aspire host: e.g. a
        // Microsoft.NET.Sdk.Web project that merely sits next to an apphost.cs. MSBuild then evaluates it
        // cleanly (exit code 0) and authoritatively reports IsAspireHost == false. That is a definitive "no",
        // so it must be rejected quietly rather than surfaced as a spurious possibly-unbuildable AppHost.
        var appHostFile = CreateProjectAppHost();
        var runner = new TestDotNetCliRunner();
        var resolver = new TestAppHostInfoResolver
        {
            GetAppHostInfoAsyncCallback = (_, _) => Task.FromResult(new AppHostProjectInfo(
                ExitCode: 0,
                IsAspireHost: false,
                AspireHostingVersion: null,
                IsUsingCliBundle: false,
                UserSecretsId: null,
                RunCommand: null,
                TargetPath: null,
                RunWorkingDirectory: null,
                RunArguments: null,
                TargetFramework: "net10.0",
                TargetFrameworks: null))
        };
        var project = CreateDotNetAppHostProject(runner, appHostInfoResolver: resolver);

        var validation = await project.ValidateAppHostAsync(appHostFile, CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.False(validation.IsPossiblyUnbuildable);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task ValidateAppHostAsync_LikelyAppHostFailsEvaluation_MarksPossiblyUnbuildable()
    {
        // A likely AppHost that MSBuild cannot evaluate (non-zero exit) may still be a real AppHost that
        // currently fails to build, so it is kept as a candidate and surfaced as possibly-unbuildable rather
        // than silently discarded.
        var appHostFile = CreateProjectAppHost();
        var runner = new TestDotNetCliRunner();
        var resolver = new TestAppHostInfoResolver
        {
            GetAppHostInfoAsyncCallback = (_, _) => Task.FromResult(new AppHostProjectInfo(
                ExitCode: 1,
                IsAspireHost: false,
                AspireHostingVersion: null,
                IsUsingCliBundle: false,
                UserSecretsId: null,
                RunCommand: null,
                TargetPath: null,
                RunWorkingDirectory: null,
                RunArguments: null,
                TargetFramework: null,
                TargetFrameworks: null))
        };
        var project = CreateDotNetAppHostProject(runner, appHostInfoResolver: resolver);

        var validation = await project.ValidateAppHostAsync(appHostFile, CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.True(validation.IsPossiblyUnbuildable);
        Assert.Equal(1, resolver.CallCount);
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

        Assert.Equal("Development", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("Production", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("Production", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:19000;http://localhost:15000", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
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
            Assert.Equal("Development", env![KnownAspNetCoreConfigNames.DotNetEnvironment]);
            Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
            Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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
            Assert.Equal("Staging", env![KnownAspNetCoreConfigNames.DotNetEnvironment]);
            Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
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
            Assert.Equal("http://localhost:15000", env[KnownAspNetCoreConfigNames.Urls]);
            Assert.Equal("Development", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
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
            Assert.Equal("http://localhost:16000", env[KnownAspNetCoreConfigNames.Urls]);
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
            Assert.Equal("http://localhost:17000", env[KnownAspNetCoreConfigNames.Urls]);
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
            Assert.Equal("Production", env![KnownAspNetCoreConfigNames.DotNetEnvironment]);
            Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
            Assert.Equal("https://localhost:19000;http://localhost:15000", env[KnownAspNetCoreConfigNames.Urls]);
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
            Assert.Equal("Staging", env![KnownAspNetCoreConfigNames.DotNetEnvironment]);
            Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
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
            Assert.Equal("Production", env![KnownAspNetCoreConfigNames.DotNetEnvironment]);
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

        Assert.Equal("https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050", env[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("https://myapp.dev.localhost:21050", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://myapp.dev.localhost:22050", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        // Run path copies profile env vars verbatim (matches what dotnet does when reading apphost.run.json natively).
        Assert.Equal("Development", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("Development", env[KnownAspNetCoreConfigNames.Environment]);
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

        Assert.Equal("https://myapp.dev.localhost:17050;http://myapp.dev.localhost:15050", env[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("https://myapp.dev.localhost:21050", env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://myapp.dev.localhost:22050", env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        // Publish path filters env-name vars from profile, then ApplyEffectiveEnvironment sets DOTNET_ENVIRONMENT=Production.
        Assert.Equal("Production", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(env.ContainsKey(KnownAspNetCoreConfigNames.Environment));
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

        Assert.Equal("https://from-run-json:19000", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("Development", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
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

        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
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

        Assert.Equal("https://localhost:17193;http://localhost:15069", env[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("Development", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
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

        Assert.Equal("Staging", env[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("https://myapp.dev.localhost:17050", env[KnownAspNetCoreConfigNames.Urls]);
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

    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithVersion_ReturnsTrue()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Aspire.AppHost.Sdk/9.5.0" />
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithoutVersion_ReturnsTrue()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Aspire.AppHost.Sdk" />
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithMultipleSdks_ReturnsTrue()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk;Aspire.AppHost.Sdk/9.5.0" />
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_NestedSdkElement_ReturnsTrue()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_NestedSdkElementInLegacyNamespace_ReturnsTrue()
    {
        // Legacy MSBuild XML namespace projects should be matched the same as SDK-style ones.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_IsAspireHostPropertyTrue_ReturnsTrue()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_IsAspireHostPropertyFalse_ReturnsFalse()
    {
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>false</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AppHostNamedProjectWithoutInlineMarker_ReturnsTrue()
    {
        // No inline Aspire marker, but the file name follows the "*.AppHost.csproj" convention, so it is a
        // candidate worth confirming via MSBuild rather than being dropped.
        var projectFile = WriteIsLikelyAppHostProject("Test.AppHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_OrdinaryProjectWithoutInlineMarker_ReturnsFalse()
    {
        // Ordinary name, parseable content, and no Aspire signal: not a candidate, so it never reaches MSBuild.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SiblingAppHostSourceFile_ReturnsTrue()
    {
        // The project has no inline marker and an ordinary name, but a sibling AppHost.cs (shipped by
        // project-based AppHosts created with `aspire new`) marks it as a candidate. The file is written with
        // its real PascalCase name so this also guards against a case-sensitive lookup missing it on
        // Linux/macOS.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("AppHost.cs", "// builder entrypoint");

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SiblingAppHostSourceFileWithDifferentCasing_ReturnsTrue()
    {
        // The sibling source-file match is case-insensitive (preserving the prior discovery behavior), so a
        // differently-cased apphost.cs next to an ordinary project is still treated as a candidate.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("apphost.cs", "// builder entrypoint");

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_UnparseableAppHostNamedCsproj_ReturnsTrue()
    {
        // Malformed XML can't be parsed, so the verdict falls back to the name heuristic. The
        // "*.AppHost.csproj" name keeps it a candidate so a broken AppHost can still surface as
        // possibly-unbuildable rather than being silently dropped.
        var projectFile = WriteIsLikelyAppHostProject("Test.AppHost.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"");

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_UnparseableOrdinaryCsproj_ReturnsFalse()
    {
        // Malformed XML falls back to the name heuristic; an ordinary name with no sibling apphost.cs is not
        // a candidate, so a broken ordinary project is not dragged onto the MSBuild path.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"");

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_FsprojWithSdkAttribute_ReturnsTrue()
    {
        // Detection is language-agnostic: the same MSBuild XML signals are inspected for .fsproj/.vbproj
        // AppHosts, not just .csproj.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.fsproj", """
            <Project Sdk="Aspire.AppHost.Sdk/9.5.0" />
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_CoLocatedDirectoryBuildPropsSetsIsAspireHost_ReturnsTrue()
    {
        // Real-world regression case: the project file has no inline marker and an ordinary name, but a
        // co-located Directory.Build.props (auto-imported by MSBuild) sets <IsAspireHost>true</IsAspireHost>,
        // which promotes it to an AppHost during evaluation. The repo's own tests/Aspire.Hosting.Tests does
        // exactly this, and it must remain a candidate.
        var projectFile = WriteIsLikelyAppHostProject("Aspire.Hosting.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_CoLocatedDirectoryBuildTargetsImportsAppHostSdk_ReturnsTrue()
    {
        // A co-located Directory.Build.targets that imports the Aspire.AppHost.Sdk also promotes an
        // otherwise ordinary-looking project to an AppHost during evaluation.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.targets", """
            <Project>
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_CoLocatedDirectoryBuildFileOnlyConsumesIsAspireHost_ReturnsFalse()
    {
        // A co-located build file that merely *reads* IsAspireHost in a condition (never sets it) must not be
        // treated as a marker. The playground's Directory.Build.targets does this with
        // Condition="'$(IsAspireHost)' == 'true'"; a loose substring match would over-promote every sibling
        // project. Matching on the property *element* threads this needle.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.targets", """
            <Project>
              <PropertyGroup Condition="'$(IsAspireHost)' == 'true'">
                <SomeSetting>value</SomeSetting>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsSetsIsAspireHost_ReturnsTrue()
    {
        // Real-world regression scenario: MSBuild walks up the directory tree to import
        // Directory.Build.props from the *nearest* ancestor that has one (not just the project's
        // own directory). A repo can park <IsAspireHost>true</IsAspireHost> in a parent
        // Directory.Build.props and ship multiple AppHost csprojs underneath it. The cheap
        // pre-check must not reject those AppHosts before MSBuild even gets a chance to evaluate
        // them, or `aspire run` against a settings/explicit path will silently fail.
        // See https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory#search-scope
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildTargetsImportsAppHostSdk_ReturnsTrue()
    {
        // Same MSBuild walk-up applies to Directory.Build.targets: a parent file importing
        // Aspire.AppHost.Sdk must promote the descendant project to an AppHost candidate.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.targets", """
            <Project>
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ImportElementWithAppHostSdkAttribute_ReturnsTrue()
    {
        // The "Import an SDK into your project" form is another supported way to bring in an
        // MSBuild SDK and is functionally equivalent to <Sdk Name="..."/>. Documented at:
        // https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk#import-an-sdk-into-your-project
        // A project using this form is a real AppHost and must survive the cheap pre-check.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="9.5.0" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ImportElementWithUnrelatedSdkAttribute_ReturnsFalse()
    {
        // Imports that pull in unrelated SDKs (e.g. AOT publishing extensions) are not AppHost
        // markers. Match on the SDK identifier rather than the mere presence of an <Import Sdk=...>
        // attribute so non-AppHost projects with custom imports are still cheaply rejected.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Sdk.props" Sdk="Some.Other.Sdk" Version="1.0.0" />
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ProjectFileContainsDynamicWalkUpImport_ReturnsTrue()
    {
        // Project-file analog of AncestorDirectoryBuildPropsContainsImportButNoMarker: a normal-named
        // .csproj whose only AppHost signal is a dynamic walk-up Import — for example
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props', ...))" />
        // pointing at a shared file that sets <IsAspireHost>true</IsAspireHost> or imports
        // Aspire.AppHost.Sdk. The cheap pre-check cannot statically resolve where the import lands,
        // so we must treat the project as a candidate and let MSBuild's authoritative evaluation
        // decide rather than silently rejecting it before ValidateAppHostAsync ever consults MSBuild.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ProjectFileContainsLowerCaseDynamicWalkUpImport_ReturnsTrue()
    {
        // MSBuild property function names are case-insensitive: evaluating
        //   $([MSBuild]::getpathoffileabove('Foo.props', '$(MSBuildThisFileDirectory)../'))
        // and
        //   $([MSBuild]::GetPathOfFileAbove('Foo.props', '$(MSBuildThisFileDirectory)../'))
        // produces the same resolved path. A case-sensitive pre-check would silently filter out the
        // lower-case variant before MSBuild evaluation, leaving the same false-negative window the
        // dynamic walk-up fallback is meant to close.
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::getpathoffileabove('Aspire.Common.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsContainsMixedCaseDynamicWalkUpImport_ReturnsTrue()
    {
        // Mixed-case variant on an ancestor Directory.Build.props — same rationale as the
        // project-file lower-case test, but at the ancestor level. Real-world Directory.Build files
        // are author-controlled and a casing tweak in upstream samples must not turn the pre-check
        // into a silent rejection path.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <Import Project="$([MSBuild]::getDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)..', 'Shared.props'))/Shared.props" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ProjectFileContainsUnrelatedStaticImports_ReturnsFalse()
    {
        // Symmetric negative for project-file imports: a normal-named .csproj that imports Arcade,
        // a common analyzer polyfill, and a relative Versions.props (all statically-named) has no
        // plausible path to declaring an Aspire marker and must NOT be promoted. Without this guard
        // the project-level dynamic-import fallback would over-promote ordinary library projects in
        // this repo, whose csprojs frequently look exactly like this.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Sdk.props" Sdk="Microsoft.DotNet.Arcade.Sdk" />
              <Import Project="NullablePolyfill.targets" />
              <Import Project="../Versions.props" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsConventionalChainingNoMarker_ReturnsFalse()
    {
        // Real shape lifted from this repo's own src/Directory.Build.props, tests/Directory.Build.props
        // and tests/Directory.Build.targets: conventional Directory.Build.* chaining via
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props',  ...))" />
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', ...))" />
        // The ancestor walk already enumerates Directory.Build.props and Directory.Build.targets at
        // every parent level by name, so the chain delivers no content the pre-check cannot already
        // see. Treating this as "uncertain" over-promotes every ordinary project under src/, tests/,
        // and similar repo layouts.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.targets"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildFileOnlyConsumesIsAspireHost_ReturnsFalse()
    {
        // Mirrors the co-located negative case: an ancestor file that merely reads $(IsAspireHost)
        // in a Condition is not a setter and must not over-promote every descendant project.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.targets", """
            <Project>
              <PropertyGroup Condition="'$(IsAspireHost)' == 'true'">
                <SomeSetting>value</SomeSetting>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ImportProjectPathContainsLiteralWalkUpHelperName_ReturnsFalse()
    {
        // The walk-up fallback must distinguish between an MSBuild *function call* — name followed
        // by `(` — and a static path that merely contains the helper name as text. Authors are free
        // to name files using the helper names; doing so does not turn the import into a dynamic
        // walk-up. Without this distinction the cheap pre-check over-promotes ordinary projects
        // whose csprojs reference files whose names happen to contain "GetPathOfFileAbove" or
        // "GetDirectoryNameOfFileAbove".
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="build/GetPathOfFileAbove.props" />
              <Import Project="../getdirectorynameoffileabove.targets" />
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ProjectFileLowerCaseIsAspireHostProperty_ReturnsTrue()
    {
        // MSBuild property names are case-insensitive: <isaspirehost>true</isaspirehost> sets
        // $(IsAspireHost) just as <IsAspireHost>true</IsAspireHost> does. Matching the property
        // element case-sensitively would silently reject a real AppHost that uses lower- or
        // mixed-case marker syntax.
        // Docs: https://learn.microsoft.com/visualstudio/msbuild/msbuild-properties
        var projectFile = WriteIsLikelyAppHostProject("MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <isaspirehost>true</isaspirehost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsMixedCaseIsAspireHostProperty_ReturnsTrue()
    {
        // Ancestor analog of the lower-case marker test. Author-controlled build files can use any
        // casing for property elements; MSBuild treats them as the same property at evaluation time,
        // so the pre-check must as well.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <ISASPIREHOST>true</ISASPIREHOST>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsContainsUnrelatedStaticImports_ReturnsFalse()
    {
        // Real shape lifted from this repo's own root Directory.Build.props/.targets: ordinary static
        // imports that pull in Arcade, common analyzer polyfills, or similar shared infrastructure but
        // never set IsAspireHost or import Aspire.AppHost.Sdk. Without this narrowing, the conservative
        // fallback would treat *every* descendant project as "possibly an AppHost" in a typical .NET
        // repo, which defeats the purpose of the cheap pre-check. Only genuinely unresolved/dynamic
        // imports (e.g. <Import Project="$([MSBuild]::GetPathOfFileAbove(...))" />) should trigger
        // the fallback; ordinary unrelated imports must not.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.DotNet.Arcade.Sdk" />
              <Import Project="NullablePolyfill.targets" />
              <Import Project="../Versions.props" />
              <PropertyGroup>
                <LangVersion>preview</LangVersion>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsContainsImportButNoMarker_ReturnsTrue()
    {
        // Real-world shape from playground/Directory.Build.targets and
        // tests/Aspire.Hosting.TestUtilities/Directory.Build.props: the file chains to a shared parent
        // with <Import Project="$([MSBuild]::GetPathOfFileAbove(...))" />. We can't follow that import
        // statically (variable-based path, conditional Imports, SDK resolution), so an AppHost marker
        // could legally live in the imported file. Conservatively treat the project as a candidate and
        // let MSBuild be authoritative rather than silently rejecting a real AppHost.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Shared.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsContainsGetDirectoryNameOfFileAboveImport_ReturnsTrue()
    {
        // GetDirectoryNameOfFileAbove is the directory-returning sibling of GetPathOfFileAbove and is
        // equally tree-walking. Treat imports that use it the same way: we cannot resolve the target
        // file statically, so let MSBuild be authoritative.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)..', 'Shared.props'))/Shared.props" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsUnreadable_ReturnsTrue()
    {
        // A relevant parent build file that exists but is malformed XML is still consumed by MSBuild
        // during evaluation, where it could declare the marker. Falling through to "no marker, return
        // false" would silently reject the project; conservatively keep it as a candidate so MSBuild's
        // evaluation surfaces the authoritative result.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", "<Project");

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorWalkStopsAtGitBoundary_ReturnsFalse()
    {
        // The ancestor walk terminates at a .git directory or file (matching AppHostInfoDiskCache's
        // walk-up bounds, Caching/AppHostInfoDiskCache.cs), so a Directory.Build.props above the .git
        // boundary must not promote a descendant project. Without this bound, an unrelated file in the
        // user's home or organization-wide profile could over-promote every project. The marker file
        // is placed in the workspace root (one level above the .git boundary in this layout) and the
        // .git marker sits next to the project's nearest parent.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("repo", "src", "MyHost", "MyHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        // Create a .git directory at the simulated repo root so the walk stops there before reaching
        // the marker file at the workspace root above it.
        Directory.CreateDirectory(Path.Combine(_workspace.WorkspaceRoot.FullName, "repo", ".git"));

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ImportProjectPathContainsCallShapedHelperNameInLiteralPath_ReturnsFalse()
    {
        // Even a literal path that happens to contain "GetPathOfFileAbove(...)" as text — not an
        // MSBuild property function call — must not trigger the walk-up fallback. Only the full
        // $([MSBuild]::GetPathOfFileAbove(...)) shape is an actual call MSBuild will evaluate.
        // Without this strictness, an author whose static import path happens to wrap the helper
        // name in parentheses over-promotes their ordinary project.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="build/GetPathOfFileAbove('Shared.props').props" />
              <Import Project="../getdirectorynameoffileabove(arg).targets" />
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_MalformedProjectXmlWithAncestorIsAspireHostMarker_ReturnsTrue()
    {
        // When the project's own XML can't be parsed, the cheap pre-check used to jump straight to
        // the name heuristic and never consult ancestor Directory.Build.* files. That silently
        // rejected an ordinary-named broken project whose ancestors declared the AppHost marker —
        // exactly the case where MSBuild would still try to evaluate the project and surface a
        // "possibly unbuildable" warning. The ancestor walk must run before falling back to the
        // name heuristic.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"");
        WriteIsLikelyAppHostProject("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_MalformedProjectXmlWithAncestorAppHostSdkImport_ReturnsTrue()
    {
        // Companion to the ancestor-IsAspireHost malformed-project test: an ancestor that imports
        // Aspire.AppHost.Sdk must also keep a broken descendant project flowing to MSBuild as a
        // candidate rather than being silently rejected on a name miss.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "MyHost", "MyHost.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"");
        WriteIsLikelyAppHostProject("Directory.Build.targets", """
            <Project>
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsUnquotedConventionalChaining_ReturnsFalse()
    {
        // MSBuild property-function arguments accept unquoted scalar forms in addition to single-
        // or double-quoted strings. Unquoted conventional chaining like
        //   $([MSBuild]::GetPathOfFileAbove(Directory.Build.props, $(MSBuildThisFileDirectory)../))
        // points at a file the ancestor walk already enumerates by name, so the chain delivers
        // nothing new — it must not be treated as uncertain just because the file name is unquoted.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Build.props, $(MSBuildThisFileDirectory)../))" />
            </Project>
            """);

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsDifferentlyCasedConventionalChaining_ReturnsTrue()
    {
        // On case-sensitive filesystems, GetPathOfFileAbove('directory.build.props', ...) can resolve
        // a different file than the exact Directory.Build.props name the ancestor walk probes. Treat
        // the chain as uncertain so MSBuild can decide rather than silently rejecting a real AppHost.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('directory.build.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsUnquotedNonConventionalImport_ReturnsTrue()
    {
        // Symmetric positive for unquoted args: an unquoted non-conventional target (Shared.props)
        // still triggers the conservative fallback because the ancestor walk does not enumerate
        // arbitrarily-named shared files.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove(Shared.props, $(MSBuildThisFileDirectory)../))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorDirectoryBuildPropsUnquotedDirectoryPackagesChaining_ReturnsTrue()
    {
        // Locks in the exact real-world shape from this repo's tests/Directory.Packages.props:2:
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))" />
        // The unquoted file name is Directory.Packages.props, which is NOT one of the conventional
        // Directory.Build.* files the ancestor walk enumerates. Even though the file looks
        // "infrastructure-like", the prefilter cannot read it, so an AppHost marker could legally
        // live there — fall back to MSBuild evaluation.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_AncestorImportAppendsCustomFileAfterGetDirectoryNameOfFileAboveAnchor_ReturnsTrue()
    {
        // GetDirectoryNameOfFileAbove returns a *directory* (the directory containing the named
        // file). A real import that uses it concatenates a file name onto that directory, e.g.
        //   $([MSBuild]::GetDirectoryNameOfFileAbove(.., 'Directory.Build.props'))/Custom.props
        // The actual imported file is the appended segment — here Custom.props — which the
        // ancestor walk does NOT enumerate by name. Looking only at the second function argument
        // (Directory.Build.props) and skipping the project would silently reject a real chain
        // pulling in arbitrary shared content.
        var projectFile = WriteIsLikelyAppHostProject(Path.Combine("src", "Library", "Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        WriteIsLikelyAppHostProject(Path.Combine("src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)..', 'Directory.Build.props'))/Custom.props" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ProjectFileImportAppendsNonConventionalFileAfterGetDirectoryNameOfFileAbove_ReturnsTrue()
    {
        // Project-file variant of the suffix-aware blocker: the same
        //   $([MSBuild]::GetDirectoryNameOfFileAbove(..., 'Directory.Build.props'))/Custom.props
        // shape can appear directly in a normal-named csproj. The captured second argument is
        // conventional, but the actual imported file is the appended Custom.props, which the
        // ancestor walk does not enumerate. Without suffix awareness the project is silently
        // rejected before MSBuild evaluation — isolated here from the ancestor-walk path so a
        // future refactor that breaks suffix extraction surfaces a clear, project-file-level
        // failure rather than only being caught at the ancestor level.
        var projectFile = WriteIsLikelyAppHostProject("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)..', 'Directory.Build.props'))/Custom.props" />
            </Project>
            """);

        Assert.True(DotNetAppHostProject.IsLikelyAppHost(projectFile));
    }

    [Theory]
    [InlineData("src/Aspire.Hosting/Aspire.Hosting.csproj")]
    [InlineData("src/Aspire.Cli/Aspire.Cli.csproj")]
    [InlineData("src/Aspire.Hosting.Yarp/Aspire.Hosting.Yarp.csproj")]
    [InlineData("src/Aspire.Hosting.Azure/Aspire.Hosting.Azure.csproj")]
    [InlineData("src/Aspire.Dashboard/Aspire.Dashboard.csproj")]
    public void IsLikelyAppHost_OrdinaryLibraryCsprojUnderSrc_ReturnsFalse(string relativePath)
    {
        // These ordinary library projects sit under repo-level Directory.Build.* files that chain to
        // parent build files. The conventional-chaining bypass and function-call-shape narrowing
        // keep those csprojs classified as not-likely-AppHost. Probing real repo files rather than
        // only synthetic shapes guards against future regressions where a refactor of
        // ContainsDynamicWalkUpImport accidentally re-broadens the fallback.
        var repoRoot = GetRepoRoot();
        if (repoRoot is null)
        {
            // Tests can run from a packaged install where the repo layout isn't present. Skip
            // rather than fail; the unit-test shapes above already lock in the algorithmic behavior.
            return;
        }

        var projectFile = new FileInfo(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!projectFile.Exists)
        {
            // A repo refactor moved the probed csproj. Don't fail the suite on file relocation —
            // the algorithmic coverage above still asserts the narrowing rules.
            return;
        }

        Assert.False(DotNetAppHostProject.IsLikelyAppHost(projectFile),
            $"{relativePath} is an ordinary library and must not classify as a likely AppHost.");
    }

    private static string? GetRepoRoot()
    {
        // Walk up from the test assembly directory until we find Aspire.slnx (the canonical repo
        // root marker, also used by AspireRepositoryDetector). Return null if we hit the filesystem
        // root without finding it, which happens when these tests run from a packaged install.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private FileInfo WriteIsLikelyAppHostProject(string fileName, string content)
    {
        var path = Path.Combine(_workspace.WorkspaceRoot.FullName, fileName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, content);
        return new FileInfo(path);
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
              </PropertyGroup>
            </Project>
            """);

        return new FileInfo(appHostPath);
    }

    private FileInfo CreateOrdinaryProject(string fileName = "Library.csproj")
    {
        // A plain SDK-style library: no AppHost markers, no imports, and a non-AppHost name.
        // This is exactly the shape DotNetAppHostProject.IsLikelyAppHost returns false for, so the
        // cheap heuristic short-circuits validation before MSBuild evaluation runs.
        var projectPath = Path.Combine(_workspace.WorkspaceRoot.FullName, fileName);
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        return new FileInfo(projectPath);
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
        IAppHostInfoResolver? appHostInfoResolver = null,
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

        if (appHostInfoResolver is not null)
        {
            services.RemoveAll<IAppHostInfoResolver>();
            services.AddSingleton(appHostInfoResolver);
        }

        var provider = services.BuildServiceProvider();
        _serviceProviders.Add(provider);
        return provider.GetRequiredService<DotNetAppHostProject>();
    }

    private static void WriteAspireConfigJson(string directory, string content)
        => File.WriteAllText(Path.Combine(directory, "aspire.config.json"), content);

    private sealed class TestAppHostInfoResolver : IAppHostInfoResolver
    {
        public Func<FileInfo, CancellationToken, Task<AppHostProjectInfo>>? GetAppHostInfoAsyncCallback { get; init; }

        public int CallCount { get; private set; }

        public Task<AppHostProjectInfo> GetAppHostInfoAsync(FileInfo projectFile, CancellationToken cancellationToken)
        {
            CallCount++;

            if (GetAppHostInfoAsyncCallback is not null)
            {
                return GetAppHostInfoAsyncCallback(projectFile, cancellationToken);
            }

            throw new InvalidOperationException("GetAppHostInfoAsync should not be called in this test.");
        }
    }
}
