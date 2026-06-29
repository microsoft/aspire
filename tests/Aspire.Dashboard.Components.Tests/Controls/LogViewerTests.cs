// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Shared.ConsoleLogs;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class LogViewerTests : DashboardTestContext
{
    [Fact]
    public void ResourcePrefixStyle_UsesGeneratedAccentAndThemeAwareTextColor()
    {
        ColorGenerator.Instance.Clear();

        var style = ResourcePrefixStyle.GetStyle("resource");
        var accentVariableName = GetBackgroundAccentVariableName(style);
        var textColorValue = GetResourceTextColor(style);

        Assert.True(
            ColorGenerator.s_variableNames.Contains(accentVariableName),
            $"Resource prefix background '{accentVariableName}' from style '{style}' must be generated from {nameof(ColorGenerator.s_variableNames)}.");
        Assert.Equal(ResourcePrefixStyle.GetTextColorValue(accentVariableName), textColorValue);
    }

    [Fact]
    public void LogViewer_RendersResourcePrefixWithGeneratedStyle()
    {
        ColorGenerator.Instance.Clear();

        SetupLogViewerServices();

        var logEntries = new LogEntries(maximumEntryCount: int.MaxValue) { BaseLineNumber = 1 };
        logEntries.InsertSorted(LogEntry.Create(
            timestamp: null,
            logMessage: "Test log message",
            rawLogContent: "Test log message",
            isErrorMessage: false,
            resourcePrefix: $"resource-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}"));

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.ShowResourcePrefix, true);
        });

        cut.WaitForAssertion(() =>
        {
            var prefixElement = Assert.Single(cut.FindAll(".resource-prefix"));
            var style = prefixElement.GetAttribute("style") ?? string.Empty;
            var accentVariableName = GetBackgroundAccentVariableName(style);
            var textColorValue = GetResourceTextColor(style);

            Assert.True(
                ColorGenerator.s_variableNames.Contains(accentVariableName),
                $"Resource prefix background '{accentVariableName}' from style '{style}' must be generated from {nameof(ColorGenerator.s_variableNames)}.");
            Assert.Equal(ResourcePrefixStyle.GetTextColorValue(accentVariableName), textColorValue);
        });
    }

    [Fact]
    public void LogViewer_FocusesAccessibleScrollContainerOnInitialRender()
    {
        SetupLogViewerServices();

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, new LogEntries(maximumEntryCount: int.MaxValue));
        });

        var scrollContainer = cut.Find("#logScrollContainer");
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        Assert.Equal("0", scrollContainer.GetAttribute("tabindex"));
        Assert.Equal("region", scrollContainer.GetAttribute("role"));
        Assert.Equal(loc[nameof(Resources.ConsoleLogs.ConsoleLogsHeader)].Value, scrollContainer.GetAttribute("aria-label"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 2 &&
                string.Equals(invocation.Arguments[0]?.ToString(), "logScrollContainer", StringComparison.Ordinal) &&
                string.Equals(invocation.Arguments[1]?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void LogViewer_FilterText_ShowsOnlyMatchingEntries()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("apple log", "banana log", "cherry log");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "banana");
        });

        cut.WaitForAssertion(() =>
        {
            var contents = cut.FindAll(".log-content").Select(e => e.TextContent.Trim()).ToList();
            var content = Assert.Single(contents);
            Assert.Contains("banana log", content);
        });
    }

    [Fact]
    public void LogViewer_FilterText_IsCaseInsensitive()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("Error connecting", "Information ready");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "ERROR");
        });

        cut.WaitForAssertion(() =>
        {
            var contents = cut.FindAll(".log-content").Select(e => e.TextContent.Trim()).ToList();
            var content = Assert.Single(contents);
            Assert.Contains("Error connecting", content);
        });
    }

    [Fact]
    public void LogViewer_FilterText_NoMatch_ShowsNoMatchMessage()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("apple log", "banana log");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "no-such-text");
        });

        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".log-content"));
            var message = Assert.Single(cut.FindAll(".console-empty-message"));
            Assert.Contains(loc[nameof(Resources.ConsoleLogs.ConsoleLogsNoLogsMatchFilter)].Value, message.TextContent);
        });
    }

    [Fact]
    public void LogViewer_FilterText_ExcludesPauseEntries()
    {
        SetupLogViewerServices();

        var pauseStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pauseEnd = pauseStart.AddSeconds(1);

        var logEntries = new LogEntries(maximumEntryCount: int.MaxValue) { BaseLineNumber = 1 };

        // A completed pause that captured filtered lines renders as a pause marker (EndTime set,
        // FilteredCount > 0). With a filter active it must be hidden because it isn't log content.
        var pauseEntry = LogEntry.CreatePause("resource", pauseStart, pauseEnd);
        pauseEntry.Pause!.FilteredCount = 2;
        logEntries.InsertSorted(pauseEntry);

        logEntries.InsertSorted(LogEntry.Create(
            timestamp: pauseStart.AddSeconds(2),
            logMessage: "matching banana log",
            rawLogContent: "matching banana log",
            isErrorMessage: false,
            resourcePrefix: null));

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "banana");
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".log-pause"));
            var content = Assert.Single(cut.FindAll(".log-content"));
            Assert.Contains("matching banana log", content.TextContent);
        });
    }

    [Fact]
    public void LogViewer_FilterText_MatchesAnsiStrippedContent()
    {
        SetupLogViewerServices();

        // Default .NET console output colors the level prefix, so ANSI escape sequences sit between
        // "info" and ":" in the raw content. The user only sees "info: Application started", so a
        // filter of "info:" must match even though the raw content is
        // "info\x1b[39m\x1b[22m\x1b[49m: Application started".
        const string rawContent = "info\x1b[39m\x1b[22m\x1b[49m: Application started";

        var logEntries = new LogEntries(maximumEntryCount: int.MaxValue) { BaseLineNumber = 1 };
        logEntries.InsertSorted(LogEntry.Create(
            timestamp: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            logMessage: "info: Application started",
            rawLogContent: rawContent,
            isErrorMessage: false,
            resourcePrefix: null));

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "info:");
        });

        cut.WaitForAssertion(() =>
        {
            var content = Assert.Single(cut.FindAll(".log-content"));
            Assert.Contains("Application started", content.TextContent);
        });
    }

    [Fact]
    public void LogViewer_FilterText_ChangingFilterUpdatesVisibleEntries()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("apple log", "banana log", "cherry log");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "apple");
        });

        cut.WaitForAssertion(() =>
        {
            var content = Assert.Single(cut.FindAll(".log-content"));
            Assert.Contains("apple log", content.TextContent);
        });

        // Changing the filter on an already-rendered component must invalidate the cached filtered
        // view and re-query Virtualize through the deferred RefreshDataAsync in OnAfterRenderAsync.
        cut.SetParametersAndRender(builder => builder.Add(p => p.FilterText, "cherry"));

        cut.WaitForAssertion(() =>
        {
            var content = Assert.Single(cut.FindAll(".log-content"));
            Assert.Contains("cherry log", content.TextContent);
        });
    }

    [Fact]
    public void LogViewer_FilterText_ClearingFilterShowsAllEntries()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("apple log", "banana log", "cherry log");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "banana");
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".log-content")));

        // A whitespace-only filter is treated as empty and must short-circuit back to showing every
        // entry, restoring the unfiltered (live buffer) view.
        cut.SetParametersAndRender(builder => builder.Add(p => p.FilterText, "   "));

        cut.WaitForAssertion(() =>
        {
            var contents = cut.FindAll(".log-content").Select(e => e.TextContent.Trim()).ToList();
            Assert.Equal(3, contents.Count);
        });
    }

    [Fact]
    public async Task LogViewer_FilterText_RefreshAfterAppendShowsNewMatchingEntries()
    {
        SetupLogViewerServices();

        var logEntries = CreateLogEntries("apple log", "banana log");

        var cut = RenderComponent<LogViewer>(builder =>
        {
            builder.Add(p => p.LogEntries, logEntries);
            builder.Add(p => p.FilterText, "banana");
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".log-content")));

        // Simulate streaming: a new matching entry is appended to the existing buffer (same reference,
        // so no parameter change fires), then the parent calls RefreshDataAsync. The cached filtered
        // snapshot must be dropped so the newly appended entry becomes visible.
        logEntries.InsertSorted(LogEntry.Create(
            timestamp: new DateTime(2024, 1, 1, 0, 0, 5, DateTimeKind.Utc),
            logMessage: "another banana log",
            rawLogContent: "another banana log",
            isErrorMessage: false,
            resourcePrefix: null));

        await cut.InvokeAsync(cut.Instance.RefreshDataAsync);

        cut.WaitForAssertion(() =>
        {
            var contents = cut.FindAll(".log-content").Select(e => e.TextContent.Trim()).ToList();
            Assert.Equal(2, contents.Count);
            Assert.Contains(contents, c => c.Contains("another banana log", StringComparison.Ordinal));
        });
    }

    private static LogEntries CreateLogEntries(params string[] messages)
    {
        var logEntries = new LogEntries(maximumEntryCount: int.MaxValue) { BaseLineNumber = 1 };
        var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var message in messages)
        {
            logEntries.InsertSorted(LogEntry.Create(
                timestamp: timestamp,
                logMessage: message,
                rawLogContent: message,
                isErrorMessage: false,
                resourcePrefix: null));
            timestamp = timestamp.AddSeconds(1);
        }

        return logEntries;
    }

    private static string GetBackgroundAccentVariableName(string style)
    {
        var background = GetCssPropertyValue(style, "background");
        Assert.True(background.StartsWith("var(", StringComparison.Ordinal), $"Unexpected background style: {style}");
        Assert.True(background.EndsWith(')'), $"Unexpected background style: {style}");

        return background[4..^1];
    }

    private static string GetResourceTextColor(string style)
    {
        var color = GetCssPropertyValue(style, "--resource-text-color");

        Assert.False(string.IsNullOrWhiteSpace(color), $"Expected resource prefix style to set --resource-text-color so LogViewer.razor.css can resolve color: var(--resource-text-color). Style: {style}");

        return color;
    }

    private static string GetCssPropertyValue(string style, string propertyName)
    {
        // Resource prefix styles are generated as:
        //   background: var(--accent-bronze); --resource-text-color: var(--accent-bronze-text, #000);
        // Split declarations because CSS values here do not contain semicolons.
        var declarations = style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var propertyPrefix = propertyName + ":";

        foreach (var declaration in declarations)
        {
            if (declaration.StartsWith(propertyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[propertyPrefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private void SetupLogViewerServices()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this, browserTimeProvider: new TestTimeProvider());
        Services.AddLogging();

        JSInterop.SetupVoid("initializeContinuousScroll").SetVoidResult();
        JSInterop.SetupVoid("resetContinuousScrollPosition").SetVoidResult();
        JSInterop.SetupVoid("focusElement", _ => true);
    }
}
