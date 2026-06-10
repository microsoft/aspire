// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Projects;

public class GuestRuntimeTests(ITestOutputHelper outputHelper)
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper));

    private GuestRuntime CreateRuntime(
        RuntimeSpec? spec = null,
        Func<string, string?>? commandResolver = null,
        ProfilingTelemetry? profilingTelemetry = null)
    {
        return new GuestRuntime(
            spec ?? CreateTestSpec(),
            _loggerFactory.CreateLogger<GuestRuntime>(),
            commandResolver: commandResolver,
            profilingTelemetry: profilingTelemetry);
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
        using var listener = CreateProfilingActivityListener(stoppedActivities.Add);
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));
        using var tempDirectory = new TestTempDirectory();

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
        var directory = new DirectoryInfo(tempDirectory.Path);
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
        var runtime = CreateRuntime(spec, commandResolver: _ => null);
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

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--output", "/out"], launcher, CancellationToken.None);

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

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--output", "/out"], launcher, CancellationToken.None);

        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal("typecheck-cmd", launcher.Calls[0].Command);
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
            CancellationToken.None,
            noBuild: true);

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

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), null, launcher, CancellationToken.None);

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

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--extra", "arg"], launcher, CancellationToken.None);

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

        var (exitCode, output) = await runtime.InstallDependenciesAsync(new DirectoryInfo("/tmp"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(output.GetLines());
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
                InstallDependencies = new CommandSpec { Command = "npm", Args = ["install"] }
            },
            commandResolver: _ => null);

        var (exitCode, output) = await runtime.InstallDependenciesAsync(new DirectoryInfo(Path.GetTempPath()), CancellationToken.None);

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
                Execute = new CommandSpec { Command = "npx", Args = ["tsx", "{appHostFile}"] }
            },
            commandResolver: _ => null);

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

            var launcher = new ProcessGuestLauncher(
                "test",
                _loggerFactory.CreateLogger<ProcessGuestLauncher>(),
                fileLoggerProvider,
                commandResolver: cmd => cmd == "dotnet" ? "dotnet" : null);

            var (exitCode, output) = await launcher.LaunchAsync(
                "dotnet",
                ["--version"],
                new DirectoryInfo(Path.GetTempPath()),
                new Dictionary<string, string>(),
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
        using var listener = CreateProfilingActivityListener(stoppedActivities.Add);
        using var profilingTelemetry = CreateProfilingTelemetry(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"));
        using var tempDirectory = new TestTempDirectory();

        var launcher = new ProcessGuestLauncher(
            "test",
            _loggerFactory.CreateLogger<ProcessGuestLauncher>(),
            commandResolver: cmd => cmd == "dotnet" ? "dotnet" : null);

        using (profilingTelemetry.StartGuestExecuteCommand(
            "test/runtime",
            "Test Runtime",
            "dotnet",
            ["--version"],
            new DirectoryInfo(tempDirectory.Path),
            ProfilingTelemetry.Values.GuestCommandPhaseExecute))
        {
            var (exitCode, output) = await launcher.LaunchAsync(
                "dotnet",
                ["--version"],
                new DirectoryInfo(tempDirectory.Path),
                new Dictionary<string, string>(),
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.NotNull(output);
            Assert.Contains(output.GetLines(), line => line.Stream == OutputLineStream.StdOut);
        }

        var activity = Assert.Single(stoppedActivities, activity =>
            activity.OperationName == ProfilingTelemetry.Activities.Process &&
            activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId) as string == "session-1" &&
            activity.GetTagItem(ProfilingTelemetry.Tags.GuestCommand) as string == "dotnet");
        Assert.Equal("process dotnet", activity.DisplayName);
        Assert.Equal("dotnet", activity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
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
    public async Task ProcessGuestLauncher_KillsProcessAndReturnsOnCancellation()
    {
        // Regression coverage for the AppHost system teardown path: when the AppHost server's
        // backchannel fails or the user cancels the run, GuestAppHostProject cancels a CTS that's
        // passed to this launcher. The launcher must kill the guest process tree (rather than
        // leaving it running) and drain output, otherwise pendingRun never completes and the CLI
        // appears to hang while it waits for the AppHost system to exit.
        var launcher = new ProcessGuestLauncher(
            "test",
            _loggerFactory.CreateLogger<ProcessGuestLauncher>());

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
        using var tempDirectory = new TestTempDirectory();
        var tempDir = tempDirectory.Path;

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
        using var tempDirectory = new TestTempDirectory();
        var tempDir = tempDirectory.Path;

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
            CancellationToken cancellationToken,
            Func<Task>? afterLaunchAsync = null)
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

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activityStopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
