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

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
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

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [extension].log");
        var executionContext = workspace.CreateExecutionContext(logFilePath: logFilePath);
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            executionContext,
            TestHelpers.CreateInteractiveHostEnvironment(),
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
}
