// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class TrayProtocolCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PsProtocolEmitsOnlyJsonIncludingInitialEmptySnapshot()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var output = new TestOutputTextWriter(outputHelper, _ => cancellation.Cancel());
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = output;
            options.InteractionServiceFactory = _ => interaction;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --protocol-version 1 --follow --format json --non-interactive --nologo");
        var exitCode = await result.InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var message = JsonSerializer.Deserialize(Assert.Single(output.Logs), TrayCliJsonContext.Default.TrayWatchMessage)!;
        Assert.Equal(1, message.Version);
        Assert.Equal("snapshot", message.Type);
        Assert.Empty(message.AppHosts!);
        Assert.Empty(interaction.ShownStatuses);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
        Assert.True(monitor.LastWatchReadOnly);
        Assert.False(monitor.LastPruneOrphanedSockets);
        Assert.True(monitor.LastThrowOnDiscoveryFailure);
        Assert.Equal(1, monitor.ScanCallCount);
    }

    [Fact]
    public async Task PsProtocolConsumerDisconnectExitsSuccessfullyWithoutErrorDiagnostics()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new TestOutputTextWriter(outputHelper, _ => throw new IOException("Broken stdout."));
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = output;
            options.InteractionServiceFactory = _ => interaction;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("ps --protocol-version 1 --follow --format json --non-interactive --nologo")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("snapshot", JsonSerializer.Deserialize(Assert.Single(output.Logs), TrayCliJsonContext.Default.TrayWatchMessage)!.Type);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedCancellations);
    }

    [Theory]
    [InlineData("ps --protocol-version 2 --follow --format json")]
    [InlineData("ps --protocol-version 1 --format json")]
    [InlineData("ps --protocol-version 1 --follow")]
    [InlineData("ps --protocol-version 1 --follow --format table")]
    public async Task PsProtocolRejectsInvalidInvocationWithoutDiscovery(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new TestOutputTextWriter(outputHelper);
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = output;
            options.InteractionServiceFactory = _ => interaction;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(arguments).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(output.Logs);
        Assert.Equal(0, monitor.ScanCallCount);
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
        Assert.Single(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData("stopped", true)]
    [InlineData("replacement", true)]
    [InlineData("not_found", false)]
    [InlineData("path_mismatch", false)]
    [InlineData("ambiguous", false)]
    [InlineData("identity_mismatch", false)]
    [InlineData("identity_unavailable", false)]
    [InlineData("stop_failed", true)]
    [InlineData("disconnected", true)]
    [InlineData("discovery_failed", false)]
    public async Task StopProtocolReportsTypedOutcomeAndNeverStopsSiblings(string scenario, bool rpcExpected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.cs");
        var output = new TestOutputTextWriter(outputHelper);
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var selected = Connection(path, int.MaxValue);
        var sibling = Connection(path, int.MaxValue - 1);
        monitor.AddConnection("selected", "selected", selected);
        monitor.AddConnection("sibling", "sibling", sibling);
        var identity = new TestProcessIdentityProvider
        {
            GetStartTime = _ => scenario == "identity_unavailable" ? null : scenario == "identity_mismatch" ? 1001 : 1000
        };
        var replacement = Connection(path, int.MaxValue);
        if (scenario == "replacement")
        {
            identity.GetStartTime = _ =>
            {
                monitor.RemoveConnection("selected", "selected");
                monitor.AddConnection("replacement", "replacement", replacement);
                return 1000;
            };
        }
        if (scenario == "not_found")
        {
            monitor.RemoveConnection("selected", "selected");
        }
        if (scenario == "ambiguous")
        {
            monitor.AddConnection("duplicate", "duplicate", Connection(path, int.MaxValue));
        }
        if (scenario == "discovery_failed")
        {
            monitor.ScanAsyncCallback = _ => throw new IOException("Discovery failed.");
        }
        selected.StopAppHostHandler = _ => scenario == "disconnected"
            ? throw new IOException("The connection disappeared.")
            : Task.FromResult(scenario != "stop_failed");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = output;
            options.InteractionServiceFactory = _ => interaction;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        services.AddSingleton<IProcessIdentityProvider>(identity);
        using var provider = services.BuildServiceProvider();
        var requestedPath = scenario == "path_mismatch" ? Path.Combine(workspace.WorkspaceRoot.FullName, "Other.cs") : path;
        var arguments = new[] { "stop", "--protocol-version", "1", "--format", "json", "--apphost", requestedPath,
            "--pid", int.MaxValue.ToString(), "--started-at", "1000", "--non-interactive", "--nologo" };
        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(arguments).InvokeAsync().DefaultTimeout();

        var message = JsonSerializer.Deserialize(Assert.Single(output.Logs), TrayCliJsonContext.Default.TrayStopMessage)!;
        Assert.Equal(1, message.Version);
        Assert.Equal(scenario switch { "replacement" => "stopped", "path_mismatch" => "not_found", "disconnected" or "discovery_failed" => "stop_failed", _ => scenario }, message.Outcome);
        Assert.Equal(exitCode, message.ExitCode);
        Assert.Equal(scenario is "stopped" or "replacement", exitCode == CliExitCodes.Success);
        Assert.Equal(rpcExpected ? 1 : 0, selected.StopAppHostCallCount);
        Assert.Equal(0, sibling.StopAppHostCallCount);
        Assert.Equal(0, replacement.StopAppHostCallCount);
        Assert.Equal(1, monitor.ScanCallCount);
        Assert.False(monitor.LastPruneOrphanedSockets);
        Assert.True(monitor.LastThrowOnDiscoveryFailure);
        Assert.Empty(interaction.ShownStatuses);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
    }

    [Theory]
    [InlineData("--protocol-version 2 --format json --pid 1 --started-at 1000", true)]
    [InlineData("--protocol-version 1 --pid 1 --started-at 1000", true)]
    [InlineData("--protocol-version 1 --format json --started-at 1000", true)]
    [InlineData("--protocol-version 1 --format json --pid 0 --started-at 1000", true)]
    [InlineData("--protocol-version 1 --format json --pid 1", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at -1", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 0", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 1000 --all", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 1000 --force", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 1000 --all false", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 1000 --force false", true)]
    [InlineData("--protocol-version 1 --format json --pid 1 --started-at 1000", false)]
    public async Task StopProtocolRejectsInvalidRequestsWithOneTypedResult(string options, bool absolutePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new TestOutputTextWriter(outputHelper);
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configuration =>
        {
            configuration.OutputTextWriter = output;
            configuration.InteractionServiceFactory = _ => interaction;
            configuration.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();
        var path = absolutePath ? Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs") : "apphost.cs";
        var result = provider.GetRequiredService<RootCommand>().Parse($"stop {options} --apphost \"{path}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        var message = JsonSerializer.Deserialize(Assert.Single(output.Logs), TrayCliJsonContext.Default.TrayStopMessage)!;
        Assert.Equal("invalid_request", message.Outcome);
        Assert.Equal(CliExitCodes.InvalidCommand, message.ExitCode);
        Assert.Equal(exitCode, message.ExitCode);
        Assert.Equal(0, monitor.ScanCallCount);
        Assert.Equal(ConsoleOutput.Error, interaction.Console);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("--format json", false)]
    [InlineData("--format table", false)]
    [InlineData("--protocol-version 0 --format json", true)]
    [InlineData("--protocol-version 2 --format json", true)]
    [InlineData("--protocol-version 1", true)]
    [InlineData("--protocol-version 1 --format table", true)]
    public async Task StopStartedAtNeverFallsBackToLegacyStop(string options, bool protocolResponseExpected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.cs");
        var output = new TestOutputTextWriter(outputHelper);
        var interaction = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var selected = Connection(path, int.MaxValue);
        var sibling = Connection(path, int.MaxValue - 1);
        monitor.AddConnection("selected", "selected", selected);
        monitor.AddConnection("sibling", "sibling", sibling);
        var identityRead = false;
        var identity = new TestProcessIdentityProvider
        {
            GetStartTime = _ =>
            {
                identityRead = true;
                return 1000;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configuration =>
        {
            configuration.OutputTextWriter = output;
            configuration.InteractionServiceFactory = _ => interaction;
            configuration.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            configuration.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        services.AddSingleton<IProcessIdentityProvider>(identity);
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"stop {options} --apphost \"{path}\" --pid {int.MaxValue} --started-at 1000 --non-interactive --nologo");
        Assert.Empty(result.Errors);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, monitor.ScanCallCount);
        Assert.False(identityRead);
        Assert.Equal(0, selected.StopAppHostCallCount);
        Assert.Equal(0, sibling.StopAppHostCallCount);
        Assert.Single(interaction.DisplayedErrors);
        if (protocolResponseExpected)
        {
            var message = JsonSerializer.Deserialize(Assert.Single(output.Logs), TrayCliJsonContext.Default.TrayStopMessage)!;
            Assert.Equal(TrayCliProtocol.Version, message.Version);
            Assert.Equal("invalid_request", message.Outcome);
            Assert.Equal(exitCode, message.ExitCode);
        }
        else
        {
            Assert.Empty(output.Logs);
        }
    }

    private static TestAppHostAuxiliaryBackchannel Connection(string path, int pid) => new()
    {
        SocketPath = Path.Combine(Path.GetDirectoryName(path)!, $"socket-{pid}"),
        AppHostInfo = new AppHostInformation { AppHostPath = path, ProcessId = pid }
    };
}
