// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandHelperTests
{
    [Fact]
    public async Task ExecuteGenericCommandAsync_WithResult_OutputsRawText()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Value = new ExecuteResourceCommandResult
                {
                    Value = "{\"items\": [\"a\", \"b\"]}",
                    Format = CommandResultFormat.Json
                }
            }
        };

        string? capturedRawText = null;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (text) => capturedRawText = text
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "generate-token",
            confirmationBinding: null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRawText);
        // Verify the raw result is passed through without any escaping
        Assert.Equal("{\"items\": [\"a\", \"b\"]}", capturedRawText);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithoutResult_DoesNotCallDisplayMessage()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };

        var displayRawTextCalled = false;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (_) => displayRawTextCalled = true
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "start",
            confirmationBinding: null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(displayRawTextCalled);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_ErrorWithResult_OutputsRawText()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Validation failed",
                Value = new ExecuteResourceCommandResult
                {
                    Value = "{\"errors\": [\"invalid host\"]}",
                    Format = CommandResultFormat.Json
                }
            }
        };

        string? capturedRawText = null;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (text) => capturedRawText = text
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "validate-config",
            confirmationBinding: null,
            CancellationToken.None).DefaultTimeout();

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(capturedRawText);
        Assert.Equal("{\"errors\": [\"invalid host\"]}", capturedRawText);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_RoutesStatusToStderr_ResultToStdout()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Value = new ExecuteResourceCommandResult
                {
                    Value = "some output",
                    Format = CommandResultFormat.Text
                }
            }
        };

        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (_) => { }
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "my-command",
            confirmationBinding: null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        // Status messages should be routed to stderr
        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmation_ExecutesWhenConfirmed()
    {
        var connection = CreateConnectionWithConfirmation("myResource", "reset", "Reset resource?");
        var interactionService = new TestInteractionService();
        interactionService.SetupBooleanResponse(true);

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "reset",
            PromptBinding.CreateDefault(false),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, connection.ExecuteResourceCommandCallCount);
        var prompt = Assert.Single(interactionService.BooleanPromptCalls);
        Assert.Equal("Reset resource?", prompt.PromptText);
        Assert.False(prompt.DefaultValue);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmation_DoesNotExecuteWhenDeclined()
    {
        var connection = CreateConnectionWithConfirmation("myResource", "reset", "Reset resource?");
        var interactionService = new TestInteractionService();
        interactionService.SetupBooleanResponse(false);

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "reset",
            PromptBinding.CreateDefault(false),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(0, connection.ExecuteResourceCommandCallCount);
        Assert.Single(interactionService.BooleanPromptCalls);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmationAndNonInteractiveOption_ExecutesWithoutPrompting()
    {
        var connection = CreateConnectionWithConfirmation("myResource", "reset", "Reset resource?");
        var interactionService = new TestInteractionService();
        var option = new Option<bool>("--non-interactive");
        var command = new System.CommandLine.Command("resource")
        {
            option
        };
        var binding = PromptBinding.Create(command.Parse("--non-interactive"), option);

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "reset",
            binding,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, connection.ExecuteResourceCommandCallCount);
        Assert.Empty(interactionService.BooleanPromptCalls);
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnectionWithConfirmation(string resourceName, string commandName, string confirmationMessage)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = resourceName,
                    DisplayName = resourceName,
                    Commands =
                    [
                        new ResourceSnapshotCommand
                        {
                            Name = commandName,
                            State = "Enabled",
                            ConfirmationMessage = confirmationMessage
                        }
                    ]
                }
            ],
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
    }
}
