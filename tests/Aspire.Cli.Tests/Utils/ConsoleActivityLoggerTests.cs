// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Tests.Utils;

public class ConsoleActivityLoggerTests
{
    private static ConsoleActivityLogger CreateLogger(StringBuilder output, bool interactive = true, bool color = true, int? width = null)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = color ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        console.Profile.Width = width ?? int.MaxValue;

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

    [Fact]
    public void WriteSummary_WithHierarchicalStepDurations_RendersIndentedTreeOrder()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(10), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            new ConsoleActivityLogger.StepDurationRecord("child-a", "Build", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(3), null, "root", 1, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
            new ConsoleActivityLogger.StepDurationRecord("grandchild", "Package", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(1), null, "child-a", 2, 3, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5)),
            new ConsoleActivityLogger.StepDurationRecord("child-b", "Publish", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, "root", 1, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)),
            new ConsoleActivityLogger.StepDurationRecord("finalize", "Finalize", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(1), null, null, 0, 5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var result = output.ToString();

        var prepareIndex = result.IndexOf("Prepare", StringComparison.Ordinal);
        var buildIndex = result.IndexOf("  Build", StringComparison.Ordinal);
        var packageIndex = result.IndexOf("    Package", StringComparison.Ordinal);
        var publishIndex = result.IndexOf("  Publish", StringComparison.Ordinal);
        var finalizeIndex = result.IndexOf("Finalize", StringComparison.Ordinal);

        Assert.True(prepareIndex >= 0);
        Assert.True(buildIndex > prepareIndex);
        Assert.True(packageIndex > buildIndex);
        Assert.True(publishIndex > packageIndex);
        Assert.True(finalizeIndex > publishIndex);
    }

    [Fact]
    public void WriteSummary_WithStepDurations_RendersTimelineScaleAndBars()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(8), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(8)),
            new ConsoleActivityLogger.StepDurationRecord("child", "Publish", ConsoleActivityLogger.ActivityState.Warning, TimeSpan.FromSeconds(2), null, "root", 1, 2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("Step timeline:", StringComparison.Ordinal));

        var scaleLine = Assert.Single(lines, line => line.Contains('┬'));
        var publishLine = Assert.Single(lines, line => line.Contains("Publish", StringComparison.Ordinal));

        Assert.Matches(@"│[─┬]+│", scaleLine);
        Assert.Contains('│', publishLine);
        Assert.True(publishLine.IndexOf('│') > publishLine.IndexOf("Publish", StringComparison.Ordinal));
        Assert.True(scaleLine.LastIndexOf('│') > scaleLine.IndexOf('│'));
        Assert.True(publishLine.Contains('╶') || publishLine.Contains('╴'));
    }

    [Fact]
    public void WriteSummary_WithMillisecondTimeline_UsesMillisecondStartLabel()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(8), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromMilliseconds(8)),
            new ConsoleActivityLogger.StepDurationRecord("child", "Publish", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(2), null, "root", 1, 2, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(6)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var timelineLabelLine = Assert.Single(lines, line => line.Contains(SharedCommandStrings.PipelineStepTimelineLabel, StringComparison.Ordinal));

        Assert.Contains("0ms", timelineLabelLine);
        Assert.DoesNotContain("0s", timelineLabelLine);
        Assert.Contains("8.00ms", timelineLabelLine);
    }

    [Fact]
    public void WriteSummary_WithSubColumnDuration_RendersPointMarker()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(100), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromMilliseconds(100)),
            new ConsoleActivityLogger.StepDurationRecord("tiny", "Package", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(0.1), null, "root", 1, 2, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10.1)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var packageLine = Assert.Single(lines, line => line.Contains("Package", StringComparison.Ordinal));

        Assert.Contains('╴', packageLine);
    }

    [Fact]
    public void WriteSummary_WithZeroDuration_UsesTimelineStartUnit()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(8), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(8)),
            new ConsoleActivityLogger.StepDurationRecord("zero", "Zero event", ConsoleActivityLogger.ActivityState.Success, TimeSpan.Zero, null, "root", 1, 2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var zeroEventLine = Assert.Single(lines, line => line.Contains("Zero event", StringComparison.Ordinal));

        Assert.Contains("0s", zeroEventLine);
        Assert.DoesNotContain("μs", zeroEventLine);
    }

    [Fact]
    public void WriteSummary_WithoutDurationRecords_DoesNotRenderStepsSummary()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false);

        logger.SetStepDurations([]);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        Assert.DoesNotContain(SharedCommandStrings.PipelineStepsSummaryTitle, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteSummary_WithDeepNesting_SkipsTimelineToPreserveStepNames()
    {
        var output = new StringBuilder();
        var logger = CreateLogger(output, interactive: false, color: false, width: 60);

        var records = new[]
        {
            new ConsoleActivityLogger.StepDurationRecord("root", "Prepare", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(8), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(8)),
            new ConsoleActivityLogger.StepDurationRecord("deep", "Publish", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, "root", 12, 2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)),
        };

        logger.SetStepDurations(records);
        logger.SetFinalResult(true);
        logger.WriteSummary();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var deepPublishLine = Assert.Single(lines, line => line.Contains("Publish", StringComparison.Ordinal));

        Assert.DoesNotContain(lines, line => line.Contains(SharedCommandStrings.PipelineStepTimelineLabel, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains('┬'));
        Assert.DoesNotContain('│', deepPublishLine);
        Assert.Contains(new string(' ', 24) + "Publish", deepPublishLine);
    }
}
