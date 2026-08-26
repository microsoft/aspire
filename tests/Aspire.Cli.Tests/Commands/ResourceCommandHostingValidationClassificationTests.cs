// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandHostingValidationClassificationTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task MetadataMissingCommandValidatesUnknownOptionBeforeExecution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = true,
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

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.NotNull(backchannel.ExecuteResourceCommandOptions);
        Assert.True(backchannel.ExecuteResourceCommandOptions.ValidateOnly);
        Assert.True(backchannel.ExecuteResourceCommandOptions.ReturnArgumentInputs);
        Assert.Equal("Unknown argument '--unknown value' for command 'configure'.", Assert.Single(interactionService.DisplayedErrors));
        Assert.Contains("Configures the browser.", output.ToString());
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public async Task CallbackFailureWithParserLikeTextDoesNotShowHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var interactionService = new TestInteractionService();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Unknown argument '--pretend' for command 'configure'."
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("message")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --message hello""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.NotNull(backchannel.ExecuteResourceCommandOptions);
        Assert.False(backchannel.ExecuteResourceCommandOptions.ValidateOnly);
        Assert.Contains("Unknown argument '--pretend' for command 'configure'.", Assert.Single(interactionService.DisplayedErrors));
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

    private static ResourceSnapshotCommandArgument CreateArgument(string name)
    {
        return new ResourceSnapshotCommandArgument
        {
            Name = name,
            InputType = "Text"
        };
    }
}
