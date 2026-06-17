// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Shared.ConsoleLogs;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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

        JSInterop.SetupVoid("initializeContinuousScroll");
        JSInterop.SetupVoid("resetContinuousScrollPosition");
    }
}
