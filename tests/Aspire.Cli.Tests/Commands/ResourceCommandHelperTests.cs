// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandHelperTests
{
    [Fact]
    public async Task ExecuteGenericCommandAsync_EscapesSpectreMarkup_InResultText()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Result = """[{"token":"eyJhbGciOiJIUzI1NiJ9","expires":"2025-01-01"}]""",
                ResultFormat = "json"
            }
        };

        string? capturedMessage = null;
        var interactionService = new TestConsoleInteractionService
        {
            DisplayMessageCallback = (emoji, message) =>
            {
                if (emoji == "clipboard")
                {
                    capturedMessage = message;
                }
            }
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "my-resource",
            "generate-token",
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedMessage);

        // Verify that Spectre markup characters are escaped.
        // Raw JSON has [ and ] which would be interpreted as markup without escaping.
        // Spectre escapes [ as [[ and ] as ]]
        Assert.Contains("[[", capturedMessage);
        Assert.Contains("]]", capturedMessage);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_NoResult_DoesNotCallDisplayMessage()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true
            }
        };

        var displayMessageCalled = false;
        var interactionService = new TestConsoleInteractionService
        {
            DisplayMessageCallback = (emoji, message) =>
            {
                if (emoji == "clipboard")
                {
                    displayMessageCalled = true;
                }
            }
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "my-resource",
            "some-command",
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(displayMessageCalled);
    }
}
