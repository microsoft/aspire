// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Interaction;

public class ExtensionInteractionServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DisplayLines_PreservesRawCompilerOutputWithBracketedProjectPath()
    {
        var backchannel = new TestExtensionBackchannel();
        DisplayLineState[]? capturedLines = null;
        backchannel.DisplayLinesAsyncCallback = lines =>
        {
            capturedLines = lines.ToArray();
            return Task.CompletedTask;
        };
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var extensionInteractionService = CreateExtensionInteractionService(backchannel, workspace);
        var compilerOutput = "Program.cs(10,5): error CS0103: The name '__AspireE2EFlushRegressionMissingSymbol__' does not exist in the current context [/tmp/AspireE2E.AppHost.csproj]";

        extensionInteractionService.DisplayLines([(OutputLineStream.StdErr, compilerOutput)]);
        await extensionInteractionService.FlushAsync();

        Assert.NotNull(capturedLines);
        var line = Assert.Single(capturedLines);
        Assert.Equal("stderr", line.Stream);
        Assert.Equal(compilerOutput, line.Line);
    }

    [Fact]
    public async Task WriteDebugSessionMessage_PreservesRawOutputWithSquareBrackets()
    {
        var backchannel = new TestExtensionBackchannel();
        string? capturedMessage = null;
        backchannel.WriteDebugSessionMessageAsyncCallback = (message, _, _) =>
        {
            capturedMessage = message;
            return Task.CompletedTask;
        };
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var extensionInteractionService = CreateExtensionInteractionService(backchannel, workspace);
        var debugOutput = "Path: $.values[0].Type | file [/tmp/AspireE2E.AppHost.csproj]";

        extensionInteractionService.WriteDebugSessionMessage(debugOutput, stdout: false, textStyle: null);
        await extensionInteractionService.FlushAsync();

        Assert.Equal(debugOutput, capturedMessage);
    }

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
            extensionPromptEnabled: false);

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

    private static ExtensionInteractionService CreateExtensionInteractionService(TestExtensionBackchannel backchannel, TemporaryWorkspace workspace)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter())
        });
        var consoleInteractionService = new ConsoleInteractionService(
            new ConsoleEnvironment(console, console),
            workspace.CreateExecutionContext(),
            TestHelpers.CreateInteractiveHostEnvironment(),
            NullLoggerFactory.Instance,
            new ConsoleLogBufferContext());

        return new ExtensionInteractionService(
            consoleInteractionService,
            backchannel,
            extensionPromptEnabled: false);
    }
}
