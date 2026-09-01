// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Interaction;

public class ExtensionInteractionServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DisplayMessage_DoesNotRenderTerminalHyperlinksToDebugConsoleCapturedOutput()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = int.MaxValue;

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            new TestExtensionBackchannel(),
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        var fileLinkMarkup = MarkupHelpers.SafeFileLink(extensionInteractionService, logFilePath);
        extensionInteractionService.DisplayMessage(
            KnownEmojis.PageFacingUp,
            string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, fileLinkMarkup),
            allowMarkup: true,
            consoleOverride: ConsoleOutput.Error);
        await extensionInteractionService.FlushAsync();

        var outputString = output.ToString();
        Assert.Contains(logFilePath, outputString);
        Assert.DoesNotContain("\u001b]8;", outputString);
        Assert.DoesNotContain("file://", outputString);
    }

    [Fact]
    public async Task DisplayCancellationMessage_WithCustomMessage_UsesCancellationBackchannel()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext();
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var cancellationMessageCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayMessageCalled = false;
        var backchannel = new TestExtensionBackchannel
        {
            DisplayCancellationMessageAsyncCalled = cancellationMessageCalled,
            DisplayMessageAsyncCallback = (_, _) =>
            {
                displayMessageCalled = true;
                return Task.CompletedTask;
            }
        };
        using var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            backchannel,
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        extensionInteractionService.DisplayCancellationMessage("Stopping dashboard.");
        await extensionInteractionService.FlushAsync();

        Assert.True(cancellationMessageCalled.Task.IsCompletedSuccessfully);
        Assert.False(displayMessageCalled);
    }

    [Fact]
    public async Task PromptForFilePathAsync_RetriesAfterInvalidSelection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invalidPath = Path.Combine(workspace.WorkspaceRoot.FullName, "invalid");
        var validPath = Path.Combine(workspace.WorkspaceRoot.FullName, "valid");
        var selections = new Queue<string?>([invalidPath, validPath]);
        var displayedErrors = new List<string>();
        var promptCount = 0;
        const string validationMessage = "The selected directory is not available.";

        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(selections.Dequeue());
            },
            DisplayErrorAsyncCallback = error =>
            {
                displayedErrors.Add(error);
                return Task.CompletedTask;
            }
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var result = await interactionService.PromptForFilePathAsync(
            "Select a directory",
            validator: path => string.Equals(path, validPath, StringComparison.Ordinal)
                ? ValidationResult.Success()
                : ValidationResult.Error(validationMessage),
            directory: true,
            retryOnValidationFailure: true);
        await interactionService.FlushAsync();

        Assert.Equal(validPath, result);
        Assert.Equal(2, promptCount);
        Assert.Equal([validationMessage], displayedErrors);
    }

    [Fact]
    public async Task PromptForFilePathAsync_CancelAfterInvalidSelectionThrows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invalidPath = Path.Combine(workspace.WorkspaceRoot.FullName, "invalid");
        var selections = new Queue<string?>([invalidPath, null]);
        var displayedErrors = new List<string>();
        var promptCount = 0;
        const string validationMessage = "The selected directory is not available.";

        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(selections.Dequeue());
            },
            DisplayErrorAsyncCallback = error =>
            {
                displayedErrors.Add(error);
                return Task.CompletedTask;
            }
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        await Assert.ThrowsAsync<ExtensionOperationCanceledException>(() =>
            interactionService.PromptForFilePathAsync(
                "Select a directory",
                validator: _ => ValidationResult.Error(validationMessage),
                directory: true,
                retryOnValidationFailure: true));
        await interactionService.FlushAsync();

        Assert.Equal(2, promptCount);
        Assert.Equal([validationMessage], displayedErrors);
    }

    [Fact]
    public async Task PromptForFilePathAsync_InvalidSelectionWithoutRetryThrows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannel = new TestExtensionBackchannel
        {
            HasCapabilityAsyncCallback = (capability, _) => Task.FromResult(capability == KnownCapabilities.FilePickers),
            PromptForFilePathAsyncCallback = (_, _, _) => Task.FromResult<string?>("invalid")
        };

        using var interactionService = CreateExtensionInteractionService(workspace, backchannel);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForFilePathAsync(
                "Select a directory",
                validator: _ => ValidationResult.Error("Invalid path.")));

        Assert.Equal("Invalid path.", exception.Message);
    }

    [Fact]
    public async Task Dispose_StopsBackgroundPump()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());
        var extensionInteractionService = new ExtensionInteractionService(
            consoleInteractionService,
            new TestExtensionBackchannel(),
            extensionPromptEnabled: false,
            logger: NullLogger<ExtensionInteractionService>.Instance);

        extensionInteractionService.Dispose();

        // The background pump should exit promptly after disposal.
        await extensionInteractionService.PumpTask.DefaultTimeout();
    }

    private static ExtensionInteractionService CreateExtensionInteractionService(
        TemporaryWorkspace workspace,
        TestExtensionBackchannel backchannel)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = int.MaxValue;

        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            workspace.CreateExecutionContext(),
            TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());

        return new ExtensionInteractionService(
            consoleInteractionService,
            backchannel,
            extensionPromptEnabled: true,
            logger: NullLogger<ExtensionInteractionService>.Instance);
    }
}
