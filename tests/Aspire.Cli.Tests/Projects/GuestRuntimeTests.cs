// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Tests;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class GuestRuntimeTests(ITestOutputHelper outputHelper)
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper));

    private ProcessGuestLauncher CreateLauncher(
        FileLoggerProvider? fileLoggerProvider = null)
        => new(
            "test",
            _loggerFactory.CreateLogger<ProcessGuestLauncher>(),
            fileLoggerProvider: fileLoggerProvider,
            processExecutionFactory: new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance));

    private GuestRuntime CreateRuntime(
        RuntimeSpec? spec = null,
        ProfilingTelemetry? profilingTelemetry = null,
        CommandSpec[]? installDependencies = null)
    {
        return new GuestRuntime(
            spec ?? CreateTestSpec(),
            _loggerFactory.CreateLogger<GuestRuntime>(),
            new TestEnvironment(),
            profilingTelemetry ?? new ProfilingTelemetry(new ConfigurationBuilder().Build()),
            installDependencies: installDependencies);
    }

    private static RuntimeSpec CreateTestSpec(
        CommandSpec? execute = null,
        CommandSpec? watchExecute = null,
        CommandSpec? publishExecute = null,
        CommandSpec? installDependencies = null,
        CommandSpec[]? preExecute = null)
    {
        return new RuntimeSpec
        {
            Language = "test/runtime",
            DisplayName = "Test Runtime",
            CodeGenLanguage = "Test",
            DetectionPatterns = ["apphost.test"],
            Execute = execute ?? new CommandSpec
            {
                Command = "test-cmd",
                Args = ["{appHostFile}"]
            },
            WatchExecute = watchExecute,
            PublishExecute = publishExecute,
            InstallDependencies = installDependencies,
            PreExecute = preExecute
        };
    }

    private static RuntimeSpec CreateTypeScriptRuntimeSpec()
    {
        return CreateTestSpec(
            execute: new CommandSpec
            {
                Command = "npx",
                Args = ["--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"]
            },
            preExecute:
            [
                new CommandSpec
                {
                    Command = "npx",
                    Args = ["--no-install", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"]
                }
            ]);
    }

    [Fact]
    public void Language_ReturnsSpecLanguage()
    {
        var runtime = CreateRuntime();

        Assert.Equal("test/runtime", runtime.Language);
    }

    [Fact]
    public void DisplayName_ReturnsSpecDisplayName()
    {
        var runtime = CreateRuntime();

        Assert.Equal("Test Runtime", runtime.DisplayName);
    }

    [Fact]
    public void CreateDefaultLauncher_ReturnsProcessGuestLauncher()
    {
        var runtime = CreateRuntime();

        var launcher = runtime.CreateDefaultLauncher();

        Assert.IsType<ProcessGuestLauncher>(launcher);
    }

    [Fact]
    public async Task RunAsync_UsesExecuteSpec()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "my-runner",
            Args = ["{appHostFile}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var envVars = new Dictionary<string, string>();

        await runtime.RunAsync(appHostFile, directory, envVars, watchMode: false, launcher, CancellationToken.None);

        Assert.Equal("my-runner", launcher.LastCommand);
        Assert.Contains(appHostFile.FullName, launcher.LastArgs);
    }

    [Fact]
    public async Task RunAsync_WatchMode_UsesWatchExecuteSpec()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            watchExecute: new CommandSpec { Command = "watch-cmd", Args = ["--watch", "{appHostFile}"] }
        );
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: true, launcher, CancellationToken.None);

        Assert.Equal("watch-cmd", launcher.LastCommand);
        Assert.Contains("--watch", launcher.LastArgs);
    }

    [Fact]
    public async Task RunAsync_WatchModeWithWatchExecute_SkipsPreExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            watchExecute: new CommandSpec { Command = "watch-cmd", Args = ["--watch", "{appHostFile}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--noEmit"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        var (exitCode, _) = await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: true, launcher, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("watch-cmd", call.Command);
    }

    [Fact]
    public async Task RunAsync_RunsPreExecuteBeforeExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--project", "{appHostDir}"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal("typecheck-cmd", launcher.Calls[0].Command);
        Assert.Equal(["--project", directory.FullName], launcher.Calls[0].Args);
        Assert.Equal("run-cmd", launcher.Calls[1].Command);
    }

    [Fact]
    public async Task RunAsync_NoBuildSkipsTypeScriptTscAndRunsAppHost()
    {
        var spec = CreateTypeScriptRuntimeSpec();
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        var (exitCode, _) = await runtime.RunAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            watchMode: false,
            launcher,
            CancellationToken.None,
            noBuild: true);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("npx", call.Command);
        Assert.Equal(["--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", appHostFile.FullName], call.Args);
    }

    [Fact]
    public async Task RunAsync_CallsAfterAppHostLaunchedAfterPreExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--project", "{appHostDir}"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var afterAppHostLaunchedCalls = 0;

        await runtime.RunAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            watchMode: false,
            launcher,
            CancellationToken.None,
            afterAppHostLaunchedAsync: () =>
            {
                afterAppHostLaunchedCalls++;
                Assert.Equal(2, launcher.Calls.Count);
                Assert.Equal("run-cmd", launcher.Calls[1].Command);
                return Task.CompletedTask;
            });

        Assert.Equal(1, afterAppHostLaunchedCalls);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal("run-cmd", launcher.Calls[1].Command);
    }

    [Fact]
    public async Task RunAsync_ProfilingTelemetryRecordsGuestCommandPhasesAndArgs()
    {
        var stoppedActivities = new ConcurrentBag<Activity>();
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStopped: stoppedActivities.Add);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var spec = CreateTestSpec(
            execute: new CommandSpec
            {
                Command = "npx",
                Args = ["tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"]
            },
            preExecute:
            [
                new CommandSpec
                {
                    Command = "npx",
                    Args = ["tsc", "--noEmit", "-p", "tsconfig.apphost.json"]
                }
            ]);
        var runtime = CreateRuntime(spec, profilingTelemetry: profilingTelemetry);
        var launcher = new RecordingLauncher();
        var directory = new DirectoryInfo(workspace.Path);
        var appHostFile = new FileInfo(Path.Combine(directory.FullName, "apphost.ts"));

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        var guestActivities = stoppedActivities
            .Where(activity => activity.OperationName == ProfilingTelemetry.Activities.Process &&
                activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId) as string == "session-1" &&
                activity.GetTagItem(ProfilingTelemetry.Tags.GuestCommandPhase) is not null)
            .OrderBy(activity => activity.StartTimeUtc)
            .ToArray();

        Assert.Collection(
            guestActivities,
            preExecuteActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Values.GuestCommandPhasePreExecute, preExecuteActivity.GetTagItem(ProfilingTelemetry.Tags.GuestCommandPhase));
                Assert.Equal("process npx", preExecuteActivity.DisplayName);
                Assert.Equal("npx", preExecuteActivity.GetTagItem(ProfilingTelemetry.Tags.GuestCommand));
                Assert.Equal(new[] { "tsc", "--noEmit", "-p", "tsconfig.apphost.json" }, Assert.IsType<string[]>(preExecuteActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(4, preExecuteActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
                Assert.Equal(0, preExecuteActivity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode));
            },
            executeActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Values.GuestCommandPhaseExecute, executeActivity.GetTagItem(ProfilingTelemetry.Tags.GuestCommandPhase));
                Assert.Equal("process npx", executeActivity.DisplayName);
                Assert.Equal("npx", executeActivity.GetTagItem(ProfilingTelemetry.Tags.GuestCommand));
                Assert.Equal(new[] { "tsx", "--tsconfig", "tsconfig.apphost.json", appHostFile.FullName }, Assert.IsType<string[]>(executeActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(4, executeActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
                Assert.Equal(0, executeActivity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode));
            });
    }

    [Fact]
    public async Task RunAsync_WhenPreExecuteFails_DoesNotExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--noEmit"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        launcher.ExitCodes.Enqueue(2);
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var afterAppHostLaunchedCalled = false;

        var (exitCode, _) = await runtime.RunAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            watchMode: false,
            launcher,
            CancellationToken.None,
            afterAppHostLaunchedAsync: () =>
            {
                afterAppHostLaunchedCalled = true;
                return Task.CompletedTask;
            });

        Assert.Equal(2, exitCode);
        Assert.False(afterAppHostLaunchedCalled);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("typecheck-cmd", call.Command);
    }

    [Fact]
    public async Task RunAsync_WhenExecuteCommandCannotResolve_DoesNotCallAfterAppHostLaunched()
    {
        var spec = CreateTestSpec(execute: new CommandSpec { Command = "missing-cmd", Args = ["{appHostFile}"] });
        var runtime = CreateRuntime(spec);
        var launcher = runtime.CreateDefaultLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var afterAppHostLaunchedCalled = false;

        var (exitCode, _) = await runtime.RunAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            watchMode: false,
            launcher,
            CancellationToken.None,
            afterAppHostLaunchedAsync: () =>
            {
                afterAppHostLaunchedCalled = true;
                return Task.CompletedTask;
            });

        Assert.Equal(-1, exitCode);
        Assert.False(afterAppHostLaunchedCalled);
    }

    [Fact]
    public async Task RunAsync_WatchModeWithoutWatchSpec_FallsBackToExecute()
    {
        var spec = CreateTestSpec(execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: true, launcher, CancellationToken.None);

        Assert.Equal("run-cmd", launcher.LastCommand);
    }

    [Fact]
    public async Task PublishAsync_UsesPublishExecuteSpec()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            publishExecute: new CommandSpec { Command = "publish-cmd", Args = ["{appHostFile}", "{args}"] }
        );
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--output", "/out"], launcher, cancellationToken: CancellationToken.None);

        Assert.Equal("publish-cmd", launcher.LastCommand);
        Assert.Contains(launcher.LastArgs, a => a.Contains("--output") && a.Contains("/out"));
    }

    [Fact]
    public async Task PublishAsync_RunsPreExecuteBeforePublishExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            publishExecute: new CommandSpec { Command = "publish-cmd", Args = ["{appHostFile}", "{args}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--project", "{appHostDir}"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--output", "/out"], launcher, cancellationToken: CancellationToken.None);

        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal("typecheck-cmd", launcher.Calls[0].Command);
        Assert.Equal("publish-cmd", launcher.Calls[1].Command);
    }

    [Fact]
    public async Task PublishAsync_CallsAfterAppHostLaunchedAfterPreExecute()
    {
        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] },
            publishExecute: new CommandSpec { Command = "publish-cmd", Args = ["{appHostFile}", "{args}"] },
            preExecute:
            [
                new CommandSpec { Command = "typecheck-cmd", Args = ["--project", "{appHostDir}"] }
            ]);
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var afterAppHostLaunchedCalls = 0;

        await runtime.PublishAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            ["--output", "/out"],
            launcher,
            afterAppHostLaunchedAsync: () =>
            {
                afterAppHostLaunchedCalls++;
                Assert.Equal(2, launcher.Calls.Count);
                Assert.Equal("publish-cmd", launcher.Calls[1].Command);
                return Task.CompletedTask;
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, afterAppHostLaunchedCalls);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal("publish-cmd", launcher.Calls[1].Command);
    }

    [Fact]
    public async Task PublishAsync_NoBuildSkipsTypeScriptTscAndRunsAppHost()
    {
        var spec = CreateTypeScriptRuntimeSpec();
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        var (exitCode, _) = await runtime.PublishAsync(
            appHostFile,
            directory,
            new Dictionary<string, string>(),
            ["--operation", "publish"],
            launcher,
            noBuild: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, exitCode);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("npx", call.Command);
        Assert.Equal(["--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", appHostFile.FullName, "--operation", "publish"], call.Args);
    }

    [Fact]
    public async Task PublishAsync_WithoutPublishSpec_FallsBackToExecute()
    {
        var spec = CreateTestSpec(execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), null, launcher, cancellationToken: CancellationToken.None);

        Assert.Equal("run-cmd", launcher.LastCommand);
    }

    [Fact]
    public async Task RunAsync_MergesSpecEnvironmentVariables()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{appHostFile}"],
            EnvironmentVariables = new Dictionary<string, string> { ["SPEC_VAR"] = "spec_value" }
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var envVars = new Dictionary<string, string> { ["CALLER_VAR"] = "caller_value" };

        await runtime.RunAsync(appHostFile, directory, envVars, watchMode: false, launcher, CancellationToken.None);

        Assert.Equal("caller_value", launcher.LastEnvironmentVariables["CALLER_VAR"]);
        Assert.Equal("spec_value", launcher.LastEnvironmentVariables["SPEC_VAR"]);
    }

    [Fact]
    public async Task RunAsync_SpecEnvironmentVariables_TakePrecedence()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{appHostFile}"],
            EnvironmentVariables = new Dictionary<string, string> { ["SHARED_VAR"] = "from_spec" }
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var envVars = new Dictionary<string, string> { ["SHARED_VAR"] = "from_caller" };

        await runtime.RunAsync(appHostFile, directory, envVars, watchMode: false, launcher, CancellationToken.None);

        Assert.Equal("from_spec", launcher.LastEnvironmentVariables["SHARED_VAR"]);
    }

    [Fact]
    public async Task RunAsync_CallerEnvironmentVariables_WithCasingAliasUseLaterValue()
    {
        var runtime = CreateRuntime(CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{appHostFile}"]
        }));
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");
        var envVars = new Dictionary<string, string>
        {
            ["PATH"] = "from_ambient",
            ["Path"] = "from_profile"
        };

        await runtime.RunAsync(appHostFile, directory, envVars, watchMode: false, launcher, CancellationToken.None);

        Assert.Single(launcher.LastEnvironmentVariables);
        Assert.Equal("from_profile", launcher.LastEnvironmentVariables["PATH"]);
    }

    [Fact]
    public async Task RunAsync_ResolvesPreExecuteAndExecuteCommandsFromEffectiveEnvironment()
    {
        var root = Directory.CreateTempSubdirectory("aspire-runtime-java-path-");
        try
        {
            var ambientJava = CreateExecutable(root, "jdk-21", "java");
            var effectiveJava = CreateExecutable(root, "jdk-25", "java");
            var effectivePath = Path.GetDirectoryName(effectiveJava)!;
            var commandEnvironment = new Dictionary<string, string> { ["PATH"] = effectivePath };
            var spec = CreateTestSpec(
                execute: new CommandSpec
                {
                    Command = "java",
                    Args = ["AppHost"],
                    EnvironmentVariables = commandEnvironment
                },
                preExecute:
                [
                    new CommandSpec
                    {
                        Command = "java",
                        Args = ["--version"],
                        EnvironmentVariables = commandEnvironment
                    }
                ]);
            var processExecutionFactory = new TestProcessExecutionFactory();
            var runtime = CreateRuntime(spec);
            var launcher = new ProcessGuestLauncher(
                "java",
                _loggerFactory.CreateLogger<ProcessGuestLauncher>(),
                fileLoggerProvider: null,
                processExecutionFactory);
            var appHostFile = new FileInfo(Path.Combine(root.FullName, "AppHost.java"));

            var (exitCode, _) = await runtime.RunAsync(
                appHostFile,
                root,
                new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(ambientJava)! },
                watchMode: false,
                launcher,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Collection(
                processExecutionFactory.CreatedExecutions,
                preExecute => Assert.Equal(
                    effectiveJava,
                    preExecute.FileName,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
                execute => Assert.Equal(
                    effectiveJava,
                    execute.FileName,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ReplacesAppHostFilePlaceholder()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "npx",
            Args = ["tsx", "{appHostFile}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/home/user/project/apphost.ts");
        var directory = new DirectoryInfo("/home/user/project");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Equal("npx", launcher.LastCommand);
        Assert.Equal(new[] { "tsx", appHostFile.FullName }, launcher.LastArgs);
    }

    [Fact]
    public async Task RunAsync_ReplacesAppHostDirPlaceholder()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["--dir", "{appHostDir}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/home/user/project/apphost.ts");
        var directory = new DirectoryInfo("/home/user/project");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Equal(new[] { "--dir", directory.FullName }, launcher.LastArgs);
    }

    [Fact]
    public async Task PublishAsync_AdditionalArgsAppendedWhenNoPlaceholder()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{appHostFile}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--extra", "arg"], launcher, cancellationToken: CancellationToken.None);

        Assert.Equal(appHostFile.FullName, launcher.LastArgs[0]);
        Assert.Equal("--extra", launcher.LastArgs[1]);
        Assert.Equal("arg", launcher.LastArgs[2]);
    }

    [Fact]
    public async Task RunAsync_EmptyPlaceholderReplacementsAreSkipped()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{args}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Empty(launcher.LastArgs);
    }

    [Fact]
    public void ExtensionLaunchCapability_ReturnsSpecValue()
    {
        var spec = new RuntimeSpec
        {
            Language = "test/runtime",
            DisplayName = "Test Runtime",
            CodeGenLanguage = "Test",
            DetectionPatterns = ["apphost.test"],
            Execute = new CommandSpec { Command = "test-cmd", Args = ["{appHostFile}"] },
            ExtensionLaunchCapability = "node"
        };
        var runtime = CreateRuntime(spec);

        Assert.Equal("node", runtime.ExtensionLaunchCapability);
    }

    [Fact]
    public void ExtensionLaunchCapability_DefaultsToNull()
    {
        var runtime = CreateRuntime();

        Assert.Null(runtime.ExtensionLaunchCapability);
    }

    [Fact]
    public async Task InstallDependenciesAsync_WithNoSpec_ReturnsZero()
    {
        var spec = CreateTestSpec();
        var runtime = CreateRuntime(spec);

        var (exitCode, output) = await runtime.InstallDependenciesAsync(
            new DirectoryInfo("/tmp"),
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(output.GetLines());
    }

    [Fact]
    public async Task InstallDependenciesAsync_WithAnInternalCommandSequence_RunsEveryCommand()
    {
        var runtime = CreateRuntime(
            installDependencies:
            [
                new CommandSpec { Command = "dotnet", Args = ["--version"] },
                new CommandSpec { Command = "aspire-command-that-does-not-exist", Args = [] }
            ]);

        var (exitCode, output) = await runtime.InstallDependenciesAsync(
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal(-1, exitCode);
        Assert.Collection(
            output.GetLines(),
            line => Assert.Equal(
                "Command 'aspire-command-that-does-not-exist' not found. Please ensure it is installed and in your PATH.",
                line.Line));
    }

    [Fact]
    public async Task InstallDependenciesAsync_MergesChildEnvironmentIntoEveryCommand()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var scriptPath = Path.Combine(
                temporaryDirectory.FullName,
                OperatingSystem.IsWindows() ? "check-environment.cmd" : "check-environment.sh");
            var script = OperatingSystem.IsWindows()
                ? """
                  @echo off
                  if not "%CHILD_VALUE%"=="from-child" exit /b 1
                  if not "%OVERRIDDEN_VALUE%"=="%1" exit /b 2
                  """
                : """
                  #!/bin/sh
                  [ "$CHILD_VALUE" = "from-child" ] && [ "$OVERRIDDEN_VALUE" = "$1" ]
                  """;
            await File.WriteAllTextAsync(scriptPath, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            var command = OperatingSystem.IsWindows() ? "cmd.exe" : scriptPath;
            string[] GetArguments(string expectedValue) => OperatingSystem.IsWindows()
                ? ["/d", "/c", scriptPath, expectedValue]
                : [expectedValue];

            var runtime = CreateRuntime(
                installDependencies:
                [
                    new CommandSpec
                    {
                        Command = command,
                        Args = GetArguments("from-first-command"),
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["overridden_value"] = "from-first-command"
                        }
                    },
                    new CommandSpec
                    {
                        Command = command,
                        Args = GetArguments("from-second-command"),
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["OVERRIDDEN_VALUE"] = "from-second-command"
                        }
                    }
                ]);

            var (exitCode, _) = await runtime.InstallDependenciesAsync(
                temporaryDirectory,
                new Dictionary<string, string>
                {
                    ["CHILD_VALUE"] = "from-child",
                    ["OVERRIDDEN_VALUE"] = "from-child"
                },
                CancellationToken.None);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstallDependenciesAsync_WhenNpmIsMissing_ReturnsNodeInstallMessage()
    {
        var runtime = CreateRuntime(
            new RuntimeSpec
            {
                Language = KnownLanguageId.TypeScript,
                DisplayName = "TypeScript (Node.js)",
                CodeGenLanguage = "typescript",
                DetectionPatterns = ["apphost.ts"],
                Execute = new CommandSpec { Command = "npx", Args = ["tsx", "{appHostFile}"] },
                InstallDependencies = new CommandSpec
                {
                    Command = "npm",
                    Args = ["install"],
                    EnvironmentVariables = new Dictionary<string, string> { ["PATH"] = string.Empty }
                }
            });

        var (exitCode, output) = await runtime.InstallDependenciesAsync(
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.Equal(-1, exitCode);
        Assert.Collection(
            output.GetLines(),
            line =>
            {
                Assert.Equal(OutputLineStream.StdErr, line.Stream);
                Assert.Equal("npm is not installed or not found in PATH. Please install Node.js and try again.", line.Line);
            });
    }

    [Fact]
    public async Task RunAsync_WhenNpxIsMissing_ReturnsNodeInstallMessage()
    {
        var runtime = CreateRuntime(
            new RuntimeSpec
            {
                Language = KnownLanguageId.TypeScript,
                DisplayName = "TypeScript (Node.js)",
                CodeGenLanguage = "typescript",
                DetectionPatterns = ["apphost.ts"],
                Execute = new CommandSpec
                {
                    Command = "npx",
                    Args = ["tsx", "{appHostFile}"],
                    EnvironmentVariables = new Dictionary<string, string> { ["PATH"] = string.Empty }
                }
            });

        var appHostFile = new FileInfo(Path.Combine(Path.GetTempPath(), "apphost.ts"));
        var (exitCode, output) = await runtime.RunAsync(
            appHostFile,
            appHostFile.Directory!,
            new Dictionary<string, string>(),
            watchMode: false,
            runtime.CreateDefaultLauncher(),
            CancellationToken.None);

        Assert.Equal(-1, exitCode);
        var resolvedOutput = Assert.IsType<OutputCollector>(output);
        Assert.Collection(
            resolvedOutput.GetLines(),
            line =>
            {
                Assert.Equal(OutputLineStream.StdErr, line.Stream);
                Assert.Equal("npx is not installed or not found in PATH. Please install Node.js and try again.", line.Line);
            });
    }

    [Fact]
    public async Task ProcessGuestLauncher_WritesOutputToLogFile()
    {
        var logFilePath = Path.Combine(Path.GetTempPath(), $"guest-output-test-{Guid.NewGuid()}.log");

        try
        {
            using var fileLoggerProvider = new FileLoggerProvider(logFilePath, new TestStartupErrorWriter());

            var launcher = CreateLauncher(fileLoggerProvider: fileLoggerProvider);

            var (exitCode, output) = await launcher.LaunchAsync(
                "dotnet",
                ["--version"],
                new DirectoryInfo(Path.GetTempPath()),
                new Dictionary<string, string>(),
                afterLaunchAsync: null,
                options: null,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.NotNull(output);

            // OutputCollector should have captured stdout
            var lines = output.GetLines().ToArray();
            Assert.NotEmpty(lines);

            // Dispose the provider to flush all pending writes
            fileLoggerProvider.Dispose();

            // Verify the log file was written and contains the output
            Assert.True(File.Exists(logFilePath), "Log file should exist");
            var logContents = await File.ReadAllTextAsync(logFilePath);
            Assert.Contains("[AppHost]", logContents);

            // The dotnet --version output should appear in the log
            var stdoutLine = lines.First(l => l.Stream == OutputLineStream.StdOut);
            Assert.Contains(stdoutLine.Line, logContents);
        }
        finally
        {
            if (File.Exists(logFilePath))
            {
                File.Delete(logFilePath);
            }
        }
    }

    [Fact]
    public async Task ProcessGuestLauncher_AnnotatesAmbientGuestProfilingActivity()
    {
        var stoppedActivities = new ConcurrentBag<Activity>();
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStopped: stoppedActivities.Add);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var launcher = CreateLauncher();

        using (profilingTelemetry.StartGuestExecuteCommand(
            "test/runtime",
            "Test Runtime",
            "dotnet",
            ["--version"],
            new DirectoryInfo(workspace.Path),
            ProfilingTelemetry.Values.GuestCommandPhaseExecute))
        {
            var (exitCode, output) = await launcher.LaunchAsync(
                "dotnet",
                ["--version"],
                new DirectoryInfo(workspace.Path),
                new Dictionary<string, string>(),
                afterLaunchAsync: null,
                options: null,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.NotNull(output);
            Assert.Contains(output.GetLines(), line => line.Stream == OutputLineStream.StdOut);
        }

        var activity = Assert.Single(stoppedActivities, activity =>
            activity.OperationName == ProfilingTelemetry.Activities.Process &&
            activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId) as string == "session-1" &&
            activity.GetTagItem(ProfilingTelemetry.Tags.GuestCommand) as string == "dotnet");
        var resolvedDotNet = PathLookupHelper.ResolveExecutablePath("dotnet");
        Assert.Equal($"process {Path.GetFileName(resolvedDotNet)}", activity.DisplayName);
        Assert.Equal(
            resolvedDotNet,
            Assert.IsType<string>(activity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Assert.Equal(new[] { "--version" }, Assert.IsType<string[]>(activity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
        Assert.Equal(1, activity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
        Assert.Equal(0, activity.GetTagItem(TelemetryConstants.Tags.ProcessExitCode));
        Assert.True((int)activity.GetTagItem(TelemetryConstants.Tags.ProcessPid)! > 0);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestProcessResolveStart);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestProcessResolved);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestProcessStart);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestProcessStarted);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestFirstStdout);
        Assert.Contains(activity.Events, @event => @event.Name == ProfilingTelemetry.Events.GuestProcessExited);
    }

    [Fact]
    public async Task ProcessGuestLauncher_ClosesChildStdinSoReadsObserveEof()
    {
        // Regression coverage for https://github.com/microsoft/aspire/issues/16791.
        // Before this fix, the shared process launcher allowed isolated Unix guest processes
        // to inherit the parent CLI's TTY, so a child process (e.g. `npm install` postinstall
        // scripts on macOS) could block forever while reading stdin and make `aspire new` for
        // the TypeScript starter appear to stall.
        var launcher = CreateLauncher();

        var tempDirectory = Directory.CreateTempSubdirectory("aspire-guest-stdin-");
        try
        {
            string command;
            string[] args;
            if (OperatingSystem.IsWindows())
            {
                // `set /p` reads from process stdin. Do not add a local `<nul` redirection here:
                // that would make the command observe EOF even if ProcessGuestLauncher regressed.
                command = "cmd.exe";
                args = ["/c", "set /p line= & if defined line (echo got-input) else (echo eof)"];
            }
            else
            {
                // `read` returns non-zero on EOF; the script prints `eof` and exits.
                command = "sh";
                args = ["-c", "if read line; then echo got-input; else echo eof; fi"];
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stopwatch = Stopwatch.StartNew();

            var (exitCode, output) = await launcher.LaunchAsync(
                command,
                args,
                tempDirectory,
                new Dictionary<string, string>(),
                afterLaunchAsync: null,
                options: null,
                cancellationToken: cts.Token);

            stopwatch.Stop();

            Assert.False(cts.IsCancellationRequested,
                $"Child process did not exit on its own within 10s - stdin may not have been closed. Elapsed: {stopwatch.Elapsed}.");
            Assert.Equal(0, exitCode);
            var lines = output?.GetLines().Select(l => l.Line).ToArray() ?? [];
            Assert.Contains(lines, l => l.Contains("eof", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => l.Contains("got-input", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProcessGuestLauncher_KillsProcessAndReturnsOnCancellation()
    {
        // Regression coverage for the AppHost system teardown path: when the AppHost server's
        // backchannel fails or the user cancels the run, GuestAppHostProject cancels a CTS that's
        // passed to this launcher. The launcher must kill the guest process tree (rather than
        // leaving it running) and drain output, otherwise pendingRun never completes and the CLI
        // appears to hang while it waits for the AppHost system to exit.
        var launcher = CreateLauncher();

        // Use a long-running cross-platform command. We pick something the OS resolves through PATH
        // so the launcher's CommandPathResolver succeeds without any fake.
        string command;
        string[] args;
        if (OperatingSystem.IsWindows())
        {
            // ping with a long count keeps the process alive for ~60 seconds; the kill needs to
            // actually terminate the process tree (cmd.exe -> ping.exe) for this to return.
            command = "cmd.exe";
            args = ["/c", "ping", "-n", "60", "127.0.0.1"];
        }
        else
        {
            command = "sleep";
            args = ["60"];
        }

        using var cts = new CancellationTokenSource();
        var launchTask = launcher.LaunchAsync(
            command,
            args,
            new DirectoryInfo(Path.GetTempPath()),
            new Dictionary<string, string>(),
            afterLaunchAsync: null,
            options: null,
            cts.Token);

        // Give the process a moment to actually start before cancelling so we exercise the
        // kill-after-running path, not the cancel-before-start short-circuit.
        await Task.Delay(500);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        var (exitCode, _) = await launchTask;
        stopwatch.Stop();

        // The killed process should report a non-zero exit code. Different platforms report this
        // differently (SIGKILL maps to 137 on Linux/macOS; cmd.exe and ping return their own
        // process-tree-termination codes on Windows), so we only assert "not zero".
        Assert.NotEqual(0, exitCode);

        // Most importantly, the launcher must return quickly after cancellation. Before this fix
        // it just propagated the OperationCanceledException without killing the process, so the
        // caller-owned `using var process = new Process { ... }` only disposed the handle - the
        // OS process kept running until the underlying command finished on its own. We give a
        // generous slack here so the test isn't flaky under load, but it should still be well
        // under the 60s the command would run for if not killed.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected ProcessGuestLauncher to return within 15s of cancellation but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_CreatesMissingMigrationFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var tempDir = workspace.Path;

        var migrationFileName = "tsconfig.apphost.json";
        var migrationContent = """{ "compilerOptions": { "target": "ES2022" } }""";

        var spec = new RuntimeSpec
        {
            Language = "test/runtime",
            DisplayName = "Test Runtime",
            CodeGenLanguage = "Test",
            DetectionPatterns = ["apphost.test"],
            Execute = new CommandSpec
            {
                Command = "test-cmd",
                Args = ["--tsconfig", migrationFileName, "{appHostFile}"]
            },
            MigrationFiles = new Dictionary<string, string>
            {
                [migrationFileName] = migrationContent
            }
        };

        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo(Path.Combine(tempDir, "apphost.ts"));
        var directory = new DirectoryInfo(tempDir);

        // File should not exist before run
        var migrationFilePath = Path.Combine(tempDir, migrationFileName);
        Assert.False(File.Exists(migrationFilePath));

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        // File should be created after run
        Assert.True(File.Exists(migrationFilePath));
        var writtenContent = await File.ReadAllTextAsync(migrationFilePath);
        Assert.Equal(migrationContent, writtenContent);
    }

    [Fact]
    public async Task RunAsync_DoesNotOverwriteExistingMigrationFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var tempDir = workspace.Path;

        var migrationFileName = "tsconfig.apphost.json";
        var migrationContent = """{ "compilerOptions": { "target": "ES2022" } }""";
        var existingContent = """{ "compilerOptions": { "target": "ES2020" } }""";

        // Pre-create the file with different content
        var migrationFilePath = Path.Combine(tempDir, migrationFileName);
        await File.WriteAllTextAsync(migrationFilePath, existingContent);

        var spec = new RuntimeSpec
        {
            Language = "test/runtime",
            DisplayName = "Test Runtime",
            CodeGenLanguage = "Test",
            DetectionPatterns = ["apphost.test"],
            Execute = new CommandSpec
            {
                Command = "test-cmd",
                Args = ["--tsconfig", migrationFileName, "{appHostFile}"]
            },
            MigrationFiles = new Dictionary<string, string>
            {
                [migrationFileName] = migrationContent
            }
        };

        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo(Path.Combine(tempDir, "apphost.ts"));
        var directory = new DirectoryInfo(tempDir);

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        // File should NOT be overwritten
        var writtenContent = await File.ReadAllTextAsync(migrationFilePath);
        Assert.Equal(existingContent, writtenContent);
    }

    [Fact]
    public async Task RunAsync_NoMigrationFiles_ExecutesNormally()
    {
        var spec = CreateTestSpec(execute: new CommandSpec
        {
            Command = "test-cmd",
            Args = ["{appHostFile}"]
        });
        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Equal("test-cmd", launcher.LastCommand);
    }

    [Fact]
    public async Task RunAsync_WhenPreExecuteHasNoStamp_RunsCommandAndWritesStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "classes", ".aspire-compile-stamp")));
    }

    [Fact]
    public async Task RunAsync_WhenStampIsNewerThanInputs_SkipsPreExecute()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.DoesNotContain(launcher.Calls, call => call.Command == "javac");
        Assert.Contains(launcher.Calls, call => call.Command == "java");
    }

    [Fact]
    public async Task RunAsync_WhenARequiredOutputIsMissing_RunsPreExecute()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec(requiredOutputs: [Path.Combine("classes", "AppHost.class")]));
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Theory]
    [InlineData("AppHost.java")]
    [InlineData("Helper.java")]
    [InlineData(".aspire/modules/com/example/Generated.java")]
    [InlineData("src/main/java/com/example/Service.java")]
    public async Task RunAsync_WhenAnyJavaInputIsNewerThanStamp_RunsPreExecute(string relativeInput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);

        var input = Path.Combine(workspace.WorkspaceRoot.FullName, relativeInput.Replace('/', Path.DirectorySeparatorChar));
        File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_UpToDateCheckIgnoresFilesOutsideTheDeclaredExtensions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        // A class file is an output of the compile, not an input to it. Treating it as an input would
        // make the check permanently stale: every compile rewrites these and would invalidate itself.
        // The file is created before the stamp because its *appearance* is a change to the directory,
        // which the check does notice; what must not register is the rewrite of one already there.
        var classFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.class");
        File.WriteAllText(classFile, "");
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(classFile, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.DoesNotContain(launcher.Calls, call => call.Command == "javac");
    }

    [Theory]
    [InlineData("Helper.java")]
    [InlineData(".aspire/modules/com/example/Generated.java")]
    [InlineData("src/main/java/com/example/Service.java")]
    public async Task RunAsync_WhenAJavaInputIsDeleted_RunsPreExecute(string relativeInput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        BackdateWorkspace(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-2));
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-1));

        // Deleting a source leaves every surviving input older than the stamp, so a check that only
        // compares file timestamps sees nothing at all. The class compiled from the deleted source is
        // still in the output directory and still on the runtime classpath, so the AppHost goes on
        // running against a type its own sources no longer define.
        File.Delete(Path.Combine(workspace.WorkspaceRoot.FullName, relativeInput.Replace('/', Path.DirectorySeparatorChar)));

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_WhenAnExplicitlyNamedInputChanges_RunsPreExecuteRegardlessOfItsExtension()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        var pom = Path.Combine(workspace.WorkspaceRoot.FullName, "pom.xml");
        File.WriteAllText(pom, "<project/>");
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);

        // The extension filter exists to keep a directory scan from picking up a command's own
        // outputs. A file the spec names outright is not a scan result - it was declared as an input
        // on purpose, and for Java that is how a changed pom.xml or build.gradle reaches the check.
        File.SetLastWriteTimeUtc(pom, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec(extraInputs: ["pom.xml"]));
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_WhenTheStagedDependencySetChanges_RunsPreExecute()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        var dependencies = Path.Combine(workspace.WorkspaceRoot.FullName, "target", "dependency");
        Directory.CreateDirectory(dependencies);
        File.WriteAllText(Path.Combine(dependencies, "guava-32.0.0.jar"), "");

        // Age the workspace before stamping so the restage below is genuinely newer. Stamping at
        // DateTime.UtcNow leaves the staged directory and the stamp within one clock tick on Windows,
        // where UtcNow advances in ~15ms steps, and the directory timestamp never compares greater.
        BackdateWorkspace(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-2));
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-1));

        // Bumping a dependency stages a differently-named JAR. Nothing under the source roots changes,
        // so without the staged set as an input the AppHost keeps running bytecode compiled against
        // the API of the version that is no longer on the classpath.
        File.Delete(Path.Combine(dependencies, "guava-32.0.0.jar"));
        File.WriteAllText(Path.Combine(dependencies, "guava-33.0.0.jar"), "");

        var runtime = CreateRuntime(CreateUpToDateSpec(extraInputs: [Path.Combine("target", "dependency")]));
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_WhenAStagedDependencyIsRestagedInPlace_SkipsPreExecute()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        var dependencies = Path.Combine(workspace.WorkspaceRoot.FullName, "target", "dependency");
        Directory.CreateDirectory(dependencies);
        var jar = Path.Combine(dependencies, "guava-32.0.0.jar");
        File.WriteAllText(jar, "");
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);

        // Dependency staging runs on every launch, so the JARs themselves can be rewritten with fresh
        // timestamps without the resolved set having changed. Reacting to that would recompile on
        // every single launch, which is the cost this check exists to avoid.
        File.SetLastWriteTimeUtc(jar, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec(extraInputs: [Path.Combine("target", "dependency")]));
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.DoesNotContain(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_UpToDateCheckDoesNotRecurseIntoNonRecursiveInputs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        // A directory input without the recursive marker is scanned top-level only, so churn deeper
        // inside it neither invalidates the command nor has to be walked. The tree is in place before
        // the stamp, as it would be in a real workspace; what must not register is the churn *inside*
        // it afterwards.
        var unrelated = Path.Combine(workspace.WorkspaceRoot.FullName, "vendor", "nested", "Vendored.java");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "");
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddMinutes(1));

        var spec = CreateTestSpec(
            execute: new CommandSpec { Command = "java", Args = ["AppHost"] },
            preExecute:
            [
                new CommandSpec
                {
                    Command = "javac",
                    Args = ["-d", "classes", "{appHostFile}"],
                    UpToDateCheck = new CommandUpToDateCheck
                    {
                        Inputs = ["{appHostFile}", "vendor"],
                        FileExtensions = [".java"],
                        StampFile = Path.Combine("classes", ".aspire-compile-stamp")
                    }
                }
            ]);

        var runtime = CreateRuntime(spec);
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.DoesNotContain(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_UpToDateCheckSeesAnEditToANestedPackageUnderTheAppHostDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        // javac is given no -classpath and no -sourcepath, so its source path defaults to the user
        // class path, which defaults to the current directory. A helper class in a package beside the
        // AppHost is therefore compiled implicitly, and its .class lands in the output directory the
        // AppHost runs from. Rewriting that file in place moves neither the AppHost directory's mtime
        // nor any top-level file, so a check that does not descend keeps stale bytecode.
        var nested = Path.Combine(workspace.WorkspaceRoot.FullName, "config", "Resources.java");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "class Resources { }");
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddMinutes(1));

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.Contains(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    public async Task RunAsync_UpToDateCheckIgnoresChurnInDirectoriesThatCannotHoldJavaPackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        // The output directory holds the stamp itself, and a dependency or tooling directory is not a
        // source root, so neither may drag the compile back out of date once it has settled.
        var churn = new[]
        {
            Path.Combine(workspace.WorkspaceRoot.FullName, "classes", "config", "Resources.class"),
            Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "vendor", "Vendored.java"),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".gradle", "caches", "Cached.java")
        };

        foreach (var path in churn)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        // Age the workspace before stamping. Stamping at DateTime.UtcNow leaves the real inputs and
        // the stamp within one clock tick on Windows, where UtcNow advances in ~15ms steps, so an
        // input can compare newer than the stamp and fail the check for a reason this test is not about.
        BackdateWorkspace(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-2));
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(-1));

        foreach (var path in churn)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            // Directories need Directory.SetLastWriteTimeUtc: the File overload opens the path without
            // FILE_FLAG_BACKUP_SEMANTICS, which Windows refuses for a directory handle.
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, DateTime.UtcNow.AddMinutes(1));
        }

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.DoesNotContain(launcher.Calls, call => call.Command == "javac");
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Directory permissions cannot be revoked this way on Windows, and root ignores them on Unix.")]
    public async Task RunAsync_UpToDateCheckTreatsAnUnreadableInputTreeAsOutOfDateInsteadOfThrowing()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("USER") == "root", "root bypasses directory permissions, so the traversal never fails.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);
        WriteStamp(workspace.WorkspaceRoot, DateTime.UtcNow.AddMinutes(1));

        // A recursive input containing a directory this user cannot traverse. EnumerateFiles is lazy,
        // so the UnauthorizedAccessException is raised while the foreach pulls from the enumerator --
        // enumerating outside the guarding try let it escape and abort AppHost startup entirely.
        var unreadable = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "main", "java", "locked");
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "Hidden.java"), "class Hidden { }");
        SetUnixFileModeForTest(unreadable, UnixFileMode.None);

        try
        {
            var runtime = CreateRuntime(CreateUpToDateSpec());
            var launcher = new RecordingLauncher();

            await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

            // Falling back to running the compile is the safe answer: an unreadable tree cannot be
            // proven unchanged.
            Assert.Contains(launcher.Calls, call => call.Command == "javac");
        }
        finally
        {
            // Restore traversal so TemporaryWorkspace can delete the tree.
            SetUnixFileModeForTest(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetUnixFileModeForTest(string path, UnixFileMode mode)
    {
        // The caller guards on platform, but the analyzer cannot see through [SkipOnPlatform].
#pragma warning disable CA1416
        File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
    }

    [Fact]
    public async Task RunAsync_WhenPreExecuteFails_DoesNotWriteStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateUpToDateWorkspace(workspace.WorkspaceRoot);

        var runtime = CreateRuntime(CreateUpToDateSpec());
        var launcher = new RecordingLauncher();
        launcher.ExitCodes.Enqueue(1);

        await runtime.RunAsync(appHostFile, workspace.WorkspaceRoot, new Dictionary<string, string>(), watchMode: false, launcher, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "classes", ".aspire-compile-stamp")));
    }

    private static RuntimeSpec CreateUpToDateSpec(string[]? extraInputs = null, string[]? requiredOutputs = null)
    {
        return CreateTestSpec(
            execute: new CommandSpec { Command = "java", Args = ["AppHost"] },
            preExecute:
            [
                new CommandSpec
                {
                    Command = "javac",
                    Args = ["-d", "classes", "{appHostFile}"],
                    UpToDateCheck = new CommandUpToDateCheck
                    {
                        Inputs = ["{appHostFile}", "./**", ".aspire/modules/**", "src/main/java/**", .. extraInputs ?? []],
                        Outputs = requiredOutputs,
                        FileExtensions = [".java"],
                        StampFile = Path.Combine("classes", ".aspire-compile-stamp")
                    }
                }
            ]);
    }

    private static FileInfo CreateUpToDateWorkspace(DirectoryInfo root)
    {
        var appHostFile = new FileInfo(Path.Combine(root.FullName, "AppHost.java"));
        File.WriteAllText(appHostFile.FullName, "class AppHost { }");
        File.WriteAllText(Path.Combine(root.FullName, "Helper.java"), "class Helper { }");

        var generated = Path.Combine(root.FullName, ".aspire", "modules", "com", "example");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "Generated.java"), "class Generated { }");

        var sources = Path.Combine(root.FullName, "src", "main", "java", "com", "example");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "Service.java"), "class Service { }");

        Directory.CreateDirectory(Path.Combine(root.FullName, "classes"));

        return appHostFile;
    }

    /// <summary>
    /// Ages every file and directory in the workspace so a later stamp can postdate all of them, which
    /// is what a workspace looks like after a successful compile.
    /// </summary>
    private static void BackdateWorkspace(DirectoryInfo root, DateTime timestampUtc)
    {
        foreach (var file in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, timestampUtc);
        }

        // Directories come second: creating the files above moved their parents' timestamps.
        foreach (var directory in Directory.EnumerateDirectories(root.FullName, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(directory, timestampUtc);
        }

        Directory.SetLastWriteTimeUtc(root.FullName, timestampUtc);
    }

    private static void WriteStamp(DirectoryInfo root, DateTime timestampUtc)
    {
        var stamp = Path.Combine(root.FullName, "classes", ".aspire-compile-stamp");
        Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
        File.WriteAllText(stamp, "");
        File.SetLastWriteTimeUtc(stamp, timestampUtc);
    }

    private static string CreateExecutable(DirectoryInfo root, string runtimeDirectory, string command)
    {
        var binDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, runtimeDirectory, "bin"));
        var executable = Path.Combine(binDirectory.FullName, OperatingSystem.IsWindows() ? $"{command}.exe" : command);
        File.WriteAllText(executable, string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return executable;
    }

    private sealed class RecordingLauncher : IGuestProcessLauncher
    {
        public List<(string Command, string[] Args)> Calls { get; } = [];
        public Queue<int> ExitCodes { get; } = [];
        public string LastCommand { get; private set; } = string.Empty;
        public string[] LastArgs { get; private set; } = [];
        public DirectoryInfo? LastWorkingDirectory { get; private set; }
        public IDictionary<string, string> LastEnvironmentVariables { get; private set; } = new Dictionary<string, string>();

        public async Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
            string command,
            string[] args,
            DirectoryInfo workingDirectory,
            IDictionary<string, string> environmentVariables,
            Func<Task>? afterLaunchAsync,
            GuestLaunchOptions? options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, args));
            LastCommand = command;
            LastArgs = args;
            LastWorkingDirectory = workingDirectory;
            LastEnvironmentVariables = new Dictionary<string, string>(environmentVariables);
            if (afterLaunchAsync is not null)
            {
                await afterLaunchAsync().ConfigureAwait(false);
            }

            var exitCode = ExitCodes.Count > 0 ? ExitCodes.Dequeue() : 0;
            return (exitCode, new OutputCollector());
        }
    }

    private static ProfilingTelemetry CreateProfilingTelemetry(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return new ProfilingTelemetry(configuration);
    }
}