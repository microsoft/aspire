// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Tests.Utils;

public class ConsoleActivityLoggerTests
{
    private static ConsoleActivityLogger CreateLogger(StringBuilder output, bool interactive = true, bool color = true)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = color ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var hostEnvironment = interactive
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment();

        return new ConsoleActivityLogger(console, hostEnvironment, forceColor: color);
    }

    [Fact]
    public void WriteSummary_WithMarkdownLinkInPipelineSummary_RendersClickableLink()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: true, color: true);

        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "☁️ Target", Value = "Azure", EnableMarkdown = false },
            new() { Key = "📦 Resource Group", Value = "VNetTest5 [link](https://portal.azure.com/#/resource/subscriptions/sub-id/resourceGroups/VNetTest5/overview)", EnableMarkdown = true },
            new() { Key = "🔑 Subscription", Value = "sub-id", EnableMarkdown = false },
            new() { Key = "🌐 Location", Value = "eastus", EnableMarkdown = false },
        };

        logger.SetFinalResult(true, summary);
        logger.WriteSummary();

        var result = output.ToString();

        // Verify the markdown link was converted to a Spectre link
        Assert.Contains("VNetTest5", result);

        const string expectedUrl =
            @"https://portal\.azure\.com/#/resource/subscriptions/sub-id/resourceGroups/VNetTest5/overview";
        string hyperlinkPattern =
            $@"\u001b\]8;[^;]*;{expectedUrl}\u001b\\.*link.*\u001b\]8;;\u001b\\";
        Assert.Matches(hyperlinkPattern, result);
    }

    [Fact]
    public void WriteSummary_WithMarkdownLinkInPipelineSummary_NoColor_RendersPlainTextWithUrl()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var portalUrl = "https://portal.azure.com/";
        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "📦 Resource Group", Value = $"VNetTest5 [link]({portalUrl})", EnableMarkdown = true },
        };

        logger.SetFinalResult(true, summary);
        logger.WriteSummary();

        var result = output.ToString();

        // When color is disabled, markdown links should be converted to plain text: text (url)
        Assert.Contains($"VNetTest5 link ({portalUrl})", result);
    }

    [Fact]
    public void WriteSummary_WithMarkdownLinkInPipelineSummary_ColorWithoutInteractive_RendersPlainUrl()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: true);

        var portalUrl = "https://portal.azure.com/";
        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "📦 Resource Group", Value = $"VNetTest5 [link]({portalUrl})", EnableMarkdown = true },
        };

        logger.SetFinalResult(true, summary);
        logger.WriteSummary();

        var result = output.ToString();

        // When color is enabled but interactive output is not supported,
        // HighlightMessage converts Spectre link markup to plain URLs
        Assert.Contains("VNetTest5", result);
        Assert.Contains(portalUrl, result);

        // Should NOT contain the OSC 8 hyperlink escape sequence since we're non-interactive
        Assert.DoesNotContain("\u001b]8;", result);
    }

    [Fact]
    public void WriteSummary_WithPlainTextPipelineSummary_RendersCorrectly()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: true, color: true);

        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "☁️ Target", Value = "Azure", EnableMarkdown = false },
            new() { Key = "🌐 Location", Value = "eastus", EnableMarkdown = false },
        };

        logger.SetFinalResult(true, summary);
        logger.WriteSummary();

        var result = output.ToString();

        Assert.Contains("Azure", result);
        Assert.Contains("eastus", result);
    }

    [Fact]
    public void WriteSummary_WithMarkupCharactersInContent_EscapesCorrectly()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: true, color: true);

        // Pipeline summary with markup characters in key
        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "Key [with] brackets", Value = "plain value", EnableMarkdown = false },
        };

        logger.SetFinalResult(true, summary);

        // Should not throw — markup characters in key must be escaped
        logger.WriteSummary();

        var result = output.ToString();

        // The literal bracket text in the key should appear in output (escaped, not interpreted as markup)
        Assert.Contains("[with]", result);
    }

    [Fact]
    public void WriteSummary_WithMarkupCharactersInContent_NoColor_EscapesCorrectly()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var summary = new List<BackchannelPipelineSummaryItem>
        {
            new() { Key = "Key [with] brackets", Value = "Value [bold]not bold[/]", EnableMarkdown = false },
        };

        logger.SetFinalResult(true, summary);

        // Should not throw — markup characters must be escaped in the non-color path
        logger.WriteSummary();

        var result = output.ToString();

        Assert.Contains("[with]", result);
        Assert.Contains("[bold]not bold[/]", result);
    }

    [Fact]
    public void WriteSummary_WithMarkupCharactersInFailureReason_EscapesCorrectly()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: true, color: true);

        logger.StartTask("step1", "Test Step");
        logger.Failure("step1", "Failed");

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("step1", "Test Step", ConsoleActivityLogger.ActivityState.Failure, TimeSpan.FromSeconds(1.5), "Error: Type[T] is invalid [details]")
        };
        logger.SetStepDurations(records);
        logger.SetFinalResult(false);

        // Should not throw — failure reason with brackets must be escaped
        logger.WriteSummary();

        var result = output.ToString();

        // The literal bracket text from the failure reason should appear
        Assert.Contains("Type[T]", result);
    }
}
