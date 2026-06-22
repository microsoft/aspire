// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
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
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

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
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

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
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

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
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        // Status messages should be routed to stderr
        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithArguments_PassesArgumentsToBackchannel()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };

        var interactionService = new TestInteractionService();

        // Command arguments JSON is expected to be an object, for example: { "selector": "#submit" }.
        var arguments = new JsonObject
        {
            ["selector"] = "#submit"
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "click",
            arguments,
            confirmationBinding: null,
            confirmationMessage: null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(connection.ExecuteResourceCommandArguments);
        Assert.Equal("#submit", connection.ExecuteResourceCommandArguments["selector"]!.GetValue<string>());
        Assert.True(connection.ExecuteResourceCommandOptions?.NonInteractive == true);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithValidationErrors_DisplaysArgumentErrors()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command argument validation failed.",
                ValidationErrors =
                [
                    new ResourceCommandArgumentValidationError
                    {
                        ArgumentName = "target",
                        ErrorMessage = "Target must not be prod."
                    }
                ]
            }
        };

        var interactionService = new TestInteractionService();

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "validate",
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("Failed to validate command arguments for command 'validate' on resource 'myResource'", error);
        Assert.DoesNotContain("Command argument validation failed.", error);
        Assert.Contains("--target: Target must not be prod.", error);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WhenValidationErrorsIsNull_DisplaysCommandError()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command 'ss' not available for resource 'test-resource'.",
                ValidationErrors = null!
            }
        };

        var interactionService = new TestInteractionService();

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "test-resource",
            "ss",
            arguments: null,
            confirmationBinding: null,
            confirmationMessage: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("Failed to execute command 'ss' on resource 'test-resource'", error);
        Assert.Contains("Command 'ss' not available for resource 'test-resource'.", error);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmation_ExecutesWhenConfirmed()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var interactionService = new TestInteractionService();
        interactionService.SetupBooleanResponse(true);

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "reset",
            arguments: null,
            confirmationBinding: PromptBinding.CreateDefault(false),
            confirmationMessage: "Reset resource?",
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, connection.ExecuteResourceCommandCallCount);
        var prompt = Assert.Single(interactionService.BooleanPromptCalls);
        Assert.Equal("Reset resource?", prompt.PromptText);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmation_DoesNotExecuteWhenDeclined()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var interactionService = new TestInteractionService();
        interactionService.SetupBooleanResponse(false);

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "reset",
            arguments: null,
            confirmationBinding: PromptBinding.CreateDefault(false),
            confirmationMessage: "Reset resource?",
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Equal(0, connection.ExecuteResourceCommandCallCount);
        Assert.Single(interactionService.BooleanPromptCalls);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithConfirmationAndNonInteractiveOption_ExecutesWithoutPrompting()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var interactionService = new TestInteractionService();
        var option = RootCommand.NonInteractiveOption;
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
            arguments: null,
            confirmationBinding: binding,
            confirmationMessage: "Reset resource?",
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, connection.ExecuteResourceCommandCallCount);
        Assert.Empty(interactionService.BooleanPromptCalls);
    }
}
