// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class GuestRuntimeTests
{
    private static RuntimeSpec CreateTestSpec(
        CommandSpec? execute = null,
        CommandSpec? watchExecute = null,
        CommandSpec? publishExecute = null,
        CommandSpec? installDependencies = null)
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
            InstallDependencies = installDependencies
        };
    }

    [Fact]
    public void Language_ReturnsSpecLanguage()
    {
        var runtime = new GuestRuntime(CreateTestSpec(), NullLogger.Instance);

        Assert.Equal("test/runtime", runtime.Language);
    }

    [Fact]
    public void DisplayName_ReturnsSpecDisplayName()
    {
        var runtime = new GuestRuntime(CreateTestSpec(), NullLogger.Instance);

        Assert.Equal("Test Runtime", runtime.DisplayName);
    }

    [Fact]
    public void CreateDefaultLauncher_ReturnsProcessGuestLauncher()
    {
        var runtime = new GuestRuntime(CreateTestSpec(), NullLogger.Instance);

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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.RunAsync(appHostFile, directory, new Dictionary<string, string>(), watchMode: true, launcher, CancellationToken.None);

        Assert.Equal("watch-cmd", launcher.LastCommand);
        Assert.Contains("--watch", launcher.LastArgs);
    }

    [Fact]
    public async Task RunAsync_WatchModeWithoutWatchSpec_FallsBackToExecute()
    {
        var spec = CreateTestSpec(execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] });
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
        var launcher = new RecordingLauncher();
        var appHostFile = new FileInfo("/tmp/apphost.ts");
        var directory = new DirectoryInfo("/tmp");

        await runtime.PublishAsync(appHostFile, directory, new Dictionary<string, string>(), ["--output", "/out"], launcher, CancellationToken.None);

        Assert.Equal("publish-cmd", launcher.LastCommand);
        Assert.Contains(launcher.LastArgs, a => a.Contains("--output") && a.Contains("/out"));
    }

    [Fact]
    public async Task PublishAsync_WithoutPublishSpec_FallsBackToExecute()
    {
        var spec = CreateTestSpec(execute: new CommandSpec { Command = "run-cmd", Args = ["{appHostFile}"] });
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);
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
        var runtime = new GuestRuntime(spec, NullLogger.Instance);

        Assert.Equal("node", runtime.ExtensionLaunchCapability);
    }

    [Fact]
    public void ExtensionLaunchCapability_DefaultsToNull()
    {
        var runtime = new GuestRuntime(CreateTestSpec(), NullLogger.Instance);

        Assert.Null(runtime.ExtensionLaunchCapability);
    }

    [Fact]
    public async Task InstallDependenciesAsync_WithNoSpec_ReturnsZero()
    {
        var spec = CreateTestSpec();
        var runtime = new GuestRuntime(spec, NullLogger.Instance);

        var (exitCode, output) = await runtime.InstallDependenciesAsync(new DirectoryInfo("/tmp"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(output.GetLines());
    }

    [Fact]
    public async Task InstallDependenciesAsync_WhenNpmIsMissing_ReturnsNodeInstallMessage()
    {
        var runtime = new GuestRuntime(
            new RuntimeSpec
            {
                Language = KnownLanguageId.TypeScript,
                DisplayName = "TypeScript (Node.js)",
                CodeGenLanguage = "typescript",
                DetectionPatterns = ["apphost.ts"],
                Execute = new CommandSpec { Command = "npx", Args = ["tsx", "{appHostFile}"] },
                InstallDependencies = new CommandSpec { Command = "npm", Args = ["install"] }
            },
            NullLogger.Instance,
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
        var runtime = new GuestRuntime(
            new RuntimeSpec
            {
                Language = KnownLanguageId.TypeScript,
                DisplayName = "TypeScript (Node.js)",
                CodeGenLanguage = "typescript",
                DetectionPatterns = ["apphost.ts"],
                Execute = new CommandSpec { Command = "npx", Args = ["tsx", "{appHostFile}"] }
            },
            NullLogger.Instance,
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
                NullLogger.Instance,
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

    private sealed class RecordingLauncher : IGuestProcessLauncher
    {
        public string LastCommand { get; private set; } = string.Empty;
        public string[] LastArgs { get; private set; } = [];
        public DirectoryInfo? LastWorkingDirectory { get; private set; }
        public IDictionary<string, string> LastEnvironmentVariables { get; private set; } = new Dictionary<string, string>();

        public Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
            string command,
            string[] args,
            DirectoryInfo workingDirectory,
            IDictionary<string, string> environmentVariables,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            LastArgs = args;
            LastWorkingDirectory = workingDirectory;
            LastEnvironmentVariables = new Dictionary<string, string>(environmentVariables);
            return Task.FromResult<(int, OutputCollector?)>((0, new OutputCollector()));
        }
    }
}
