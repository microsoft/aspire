// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests;

public class ProgramTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(nameof(IDocsFetcher))]
    [InlineData(nameof(IApiDocsFetcher))]
    public void DocumentationClients_EnableResponseDecompression(string clientName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Program.AddDocumentationServices(services);
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Equal(DecompressionMethods.All, socketsHandler.AutomaticDecompression);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("winget")]
    [InlineData("brew")]
    [InlineData("dotnet-tool")]
    [InlineData("nix")]
    [InlineData("pr")]
    [InlineData("localhive")]
    [InlineData("npm")]
    [InlineData("mise")]
    [InlineData("unknown")]
    public void StartMainActivity_ReportsInstallSource(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var fixture = new TelemetryFixture();
        if (source is not ("unknown" or "npm" or "mise"))
        {
            File.WriteAllText(Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName), $$"""{"source":"{{source}}"}""");
        }
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = source == "npm" ? NpmInstallDetection.ExpectedPackageName : null
        });
        var processPath = source == "mise"
            ? Path.Combine(workspace.Path, "mise", "installs", "aspire", "13.5.0", "aspire")
            : Path.Combine(workspace.Path, "aspire");
        var detector = new InstallSourceDetector(
            new TestProcessPathProvider(processPath),
            CliTestHelper.CreateSidecarReader(outputHelper),
            new TestWindowsRegistryReader(),
            environment,
            NullLogger<InstallSourceDetector>.Instance);

        using var activity = Program.StartMainActivity(fixture.Telemetry, detector);

        Assert.NotNull(activity);
        Assert.Equal(TelemetryConstants.Activities.Main, activity.OperationName);
        Assert.Equal(source, activity.GetTagItem("aspire.cli.install.source"));
        Assert.Equal(Environment.ProcessId, activity.GetTagItem(TelemetryConstants.Tags.ProcessPid));
        Assert.Equal("aspire", activity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
        activity.Stop();
        Assert.NotNull(fixture.CapturedActivity);
        Assert.Same(activity, fixture.CapturedActivity);
        Assert.Equal(source, fixture.CapturedActivity.GetTagItem(TelemetryConstants.Tags.InstallSource));

        using var otherActivity = fixture.Telemetry.StartReportedActivity("other");
        Assert.NotNull(otherActivity);
        Assert.Null(otherActivity.GetTagItem(TelemetryConstants.Tags.InstallSource));
    }

    [Fact]
    public void StartMainActivity_DoesNotProbeWhenActivityIsNotCreated()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.None);
        var detector = new InstallSourceDetector(
            new TestProcessPathProvider(Path.Combine(workspace.Path, "aspire.exe")),
            CliTestHelper.CreateSidecarReader(outputHelper),
            new TestWindowsRegistryReader
            {
                ProbeCallback = _ => throw new InvalidOperationException("No install probe should run without an activity.")
            },
            TestEnvironment.CreateWindows(),
            NullLogger<InstallSourceDetector>.Instance);

        using var activity = Program.StartMainActivity(fixture.Telemetry, detector);

        Assert.Null(activity);
    }

    [Fact]
    public void ParseLogFileOption_ReturnsNull_WhenArgsAreNull()
    {
        var result = Program.ParseLogFileOption(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLogFileOption_ReturnsValue_WhenOptionAppearsBeforeDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--log-file", "cli.log", "--", "--log-file", "app.log"]);

        Assert.Equal("cli.log", result);
    }

    [Fact]
    public void ParseLogFileOption_IgnoresValue_WhenOptionAppearsAfterDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--", "--log-file", "app.log"]);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("ps", CliExitCodes.Success, true)]
    [InlineData("ps --format json", CliExitCodes.Success, true)]
    [InlineData("describe", CliExitCodes.Success, true)]
    [InlineData("ps", CliExitCodes.FailedToFindProject, false)]
    [InlineData("ps --follow", CliExitCodes.Success, false)]
    [InlineData("describe --follow", CliExitCodes.Success, false)]
    [InlineData("ps --debug", CliExitCodes.Success, false)]
    [InlineData("ps -d", CliExitCodes.Success, false)]
    [InlineData("ps --log-level Debug", CliExitCodes.Success, false)]
    [InlineData("ps --log-level=Debug", CliExitCodes.Success, false)]
    [InlineData("ps --log-level:Debug", CliExitCodes.Success, false)]
    [InlineData("ps -l Debug", CliExitCodes.Success, false)]
    [InlineData("ps --log-file cli.log", CliExitCodes.Success, false)]
    [InlineData("ps --log-file=cli.log", CliExitCodes.Success, false)]
    [InlineData("run", CliExitCodes.Success, false)]
    public void ShouldDiscardLogOnSuccess_OnlyForCleanOneShotQueries(string commandLine, int exitCode, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>().Parse(commandLine.Split(' '));

        Assert.Empty(parseResult.Errors);
        Assert.Equal(expected, Program.ShouldDiscardLogOnSuccess(exitCode, cancellationRequested: false, parseResult));
    }

    [Theory]
    [InlineData("ps")]
    [InlineData("describe")]
    public void ShouldDiscardLogOnSuccess_KeepsLog_WhenCancelledWithSuccessExitCode(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>().Parse(commandLine.Split(' '));

        Assert.Empty(parseResult.Errors);
        Assert.False(Program.ShouldDiscardLogOnSuccess(CliExitCodes.Success, cancellationRequested: true, parseResult));
    }

    [Theory]
    [InlineData("--version --log-level bogus")]
    [InlineData("--version --log-file")]
    [InlineData("ps --help --log-level bogus")]
    [InlineData("describe --help --log-level bogus")]
    public void ShouldDiscardLogOnSuccess_KeepsLog_WhenDiagnosticOptionValueIsInvalid(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>().Parse(commandLine.Split(' '));

        Assert.False(Program.ShouldDiscardLogOnSuccess(CliExitCodes.Success, cancellationRequested: false, parseResult));
    }

    [Fact]
    public void BuildAnsiConsole_DoesNotReenablePlaygroundFormatting_WhenHostDisablesAnsi()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_PLAYGROUND"] = "true"
        }).Build());
        services.AddSingleton<ICliHostEnvironment>(new TestCliHostEnvironment(supportsAnsi: false, supportsInteractiveOutput: false));

        using var serviceProvider = services.BuildServiceProvider();
        var writer = new StringWriter(new StringBuilder());
        var buildAnsiConsole = typeof(Program).GetMethod("BuildAnsiConsole", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildAnsiConsole);

        var ansiConsole = Assert.IsAssignableFrom<Spectre.Console.IAnsiConsole>(buildAnsiConsole.Invoke(null, [serviceProvider, writer]));

        ansiConsole.MarkupLine("[red]hello[/]");

        var output = writer.ToString();
        Assert.Contains("hello", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AcquireBundleLeaseFromEnvironment_HoldsLeaseWhenBundleVersionDirectoryIsSet()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var versionDirectory = workspace.CreateDirectory("version");

        {
            using var result = RemoteExecutor.Invoke(static versionDirectory =>
            {
                Environment.SetEnvironmentVariable(BundleDiscovery.BundleVersionDirectoryEnvVar, versionDirectory);

                using var lease = Program.AcquireBundleLeaseFromEnvironment(["run"]);

                Assert.NotNull(lease);
                Assert.True(BundleVersionLease.HasActiveLease(versionDirectory));
            }, versionDirectory.FullName);
        }

        Assert.False(BundleVersionLease.HasActiveLease(versionDirectory.FullName));
    }

    [Fact]
    public void WarnIfGlobalSettingsContainAppHostPath_WritesWarning_WhenGlobalConfigHasAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, """{ "appHost": { "path": "AppHost.csproj" } }""");
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        var line = Assert.Single(errorWriter.MarkupLines);
        Assert.DoesNotContain("[yellow]", line, StringComparison.Ordinal);
        Assert.Contains(settingsPath, line, StringComparison.Ordinal);
        Assert.Contains("appHost.path", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnIfGlobalSettingsContainAppHostPath_DoesNotWarn_WhenGlobalConfigHasNoAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, """{ "channel": "daily" }""");
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("""{ "appHost": { "path": "AppHost.csproj" }""")]
    public void WarnIfGlobalSettingsContainAppHostPath_DoesNotWarn_WhenGlobalConfigCannotBeLoaded(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, content);
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        Assert.Empty(errorWriter.MarkupLines);
    }
}
