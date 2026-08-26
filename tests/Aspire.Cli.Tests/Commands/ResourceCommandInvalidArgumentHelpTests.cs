// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandInvalidArgumentHelpTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResourceCommand_InvalidCommandArgumentShowsCommandSpecificHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("message", description: "Message to send.", required: true)))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --unknown value""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        Assert.Equal("Unrecognized command option '--unknown value'.", Assert.Single(interactionService.DisplayedErrors));

        var helpOutput = output.ToString();
        Assert.Contains("Configures the browser.", helpOutput);
        Assert.Contains("Usage:", helpOutput);
        Assert.Contains("aspire resource web-browser-automation configure [options] [[--] <command-options>...]", helpOutput);
        Assert.Contains("--message <value>", helpOutput);
        Assert.Contains("Message to send. Required.", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_ExtensionErrorIsFlushedBeforeHelpIsWritten()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("message", description: "Message to send.", required: true)))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var events = new List<string>();
        TestExtensionInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => interactionService = new TestExtensionInteractionService(sp)
            {
                DisplayErrorCallback = _ => events.Add("error"),
                FlushAsyncCallback = _ =>
                {
                    events.Add("flush");
                    return Task.CompletedTask;
                }
            };
        });
        await using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --unknown value""");
        var output = new FirstWriteCallbackTextWriter(() => events.Add("help"));

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.NotNull(interactionService);
        Assert.Equal(["error", "flush", "help", "flush"], events);
        Assert.Equal(2, interactionService.FlushAsyncCallCount);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task BaseCommand_TimedOutPreFlushIsNotRetriedByFinalFlush()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        TestExtensionInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => interactionService = new TestExtensionInteractionService(sp)
            {
                FlushAsyncCallback = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            };
        });
        await using var provider = services.BuildServiceProvider();

        var command = new FlushTimeoutTestCommand(provider.GetRequiredService<CommonCommandServices>());
        var result = command.Parse(string.Empty);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.NotNull(interactionService);
        Assert.Equal(1, interactionService.FlushAsyncCallCount);
    }

    [Fact]
    public async Task ResourceCommand_LoadArgumentsInvalidInputDoesNotWriteHumanHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("message", description: "Message to send.")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --load-arguments --unknown value""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        Assert.Equal("Unrecognized command option '--unknown value'.", Assert.Single(interactionService.DisplayedErrors));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ResourceCommand_HostingUnknownArgumentShowsCommandSpecificHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Unknown argument '--unknown value' for command 'configure'."
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", "Configures the browser."))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --unknown value""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Unknown argument '--unknown value' for command 'configure'.", Assert.Single(interactionService.DisplayedErrors));

        var helpOutput = output.ToString();
        Assert.Contains("Configures the browser.", helpOutput);
        Assert.Contains("Usage:", helpOutput);
        Assert.Contains("aspire resource web-browser-automation configure [options] [[--] <command-options>...]", helpOutput);
        Assert.Contains("Options:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HostingUnknownArgumentForWellKnownCommandShowsCommandSpecificHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Unknown argument '--unknown value' for command 'start'."
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("start", "Starts the resource."))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation start --unknown value""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Unknown argument '--unknown value' for command 'start'.", Assert.Single(interactionService.DisplayedErrors));

        var helpOutput = output.ToString();
        Assert.Contains("Starts the resource.", helpOutput);
        Assert.Contains("Usage:", helpOutput);
        Assert.Contains("aspire resource web-browser-automation start [options] [[--] <command-options>...]", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_OrdinaryExecutionFailureDoesNotShowCommandHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command execution failed."
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", "Configures the browser."))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Command execution failed.", Assert.Single(interactionService.DisplayedErrors));
        Assert.Empty(output.ToString());
    }

    private ServiceProvider CreateServiceProvider(
        TemporaryWorkspace workspace,
        TestAppHostAuxiliaryBackchannel backchannel,
        TestInteractionService interactionService)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });

        return services.BuildServiceProvider();
    }

    private static ResourceSnapshot CreateResourceSnapshot(string name, params ResourceSnapshotCommand[] commands)
    {
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            State = "Running",
            Commands = commands
        };
    }

    private static ResourceSnapshotCommand CreateCommand(string name, string description, params ResourceSnapshotCommandArgument[] argumentInputs)
    {
        return new ResourceSnapshotCommand
        {
            Name = name,
            Description = description,
            State = "Enabled",
            Visibility = KnownCommandVisibility.Default,
            ArgumentInputs = argumentInputs
        };
    }

    private static ResourceSnapshotCommandArgument CreateArgument(string name, string? description = null, bool required = false)
    {
        return new ResourceSnapshotCommandArgument
        {
            Name = name,
            Description = description,
            InputType = "Text",
            Required = required
        };
    }

    private sealed class FirstWriteCallbackTextWriter(Action onFirstWrite) : StringWriter
    {
        private bool _hasWritten;

        public override void WriteLine(string? value)
        {
            if (!_hasWritten)
            {
                _hasWritten = true;
                onFirstWrite();
            }

            base.WriteLine(value);
        }
    }

    private sealed class FlushTimeoutTestCommand(CommonCommandServices services) : BaseCommand("flush-timeout-test", "Tests extension flush timeout behavior.", services)
    {
        protected override TimeSpan ExtensionInteractionFlushTimeout => TimeSpan.FromMilliseconds(20);

        protected override async Task<CommandResult> ExecuteAsync(System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
        {
            await FlushExtensionInteractionServiceAsync(InteractionService).ConfigureAwait(false);
            return CommandResult.Success();
        }
    }
}
