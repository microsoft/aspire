// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class TerminalTitleTests : DashboardTestContext
{
    public TerminalTitleTests()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        TerminalSetupHelpers.SetupTerminalTitle(this);
    }

    [Fact]
    public void Metadata_IsRenderedAsTextAndClearsToFallback()
    {
        var cut = RenderComponent<TerminalTitle>(builder => builder
            .Add(p => p.FallbackTitle, "shell")
            .Add(p => p.State, new TerminalToolbarState
            {
                Title = "<script>title</script>",
                WorkingDirectory = "/work/<app>",
                WorkingDirectoryUri = "file://remote/work/%3Capp%3E"
            }));

        Assert.Equal("<script>title</script>", cut.Find(".terminal-title").TextContent);
        var button = cut.Find(".terminal-directory");
        Assert.Equal("/work/<app>", cut.Find(".terminal-directory-measure").TextContent);
        Assert.Equal("/work/<app>", button.GetAttribute("data-text"));
        Assert.Equal("true", button.GetAttribute("data-copybutton"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopyToClipboard, button.GetAttribute("data-precopy"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopied, button.GetAttribute("data-postcopy"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("Copy working directory: /work/<app>", button.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("fluent-tooltip"));
        Assert.False(button.HasAttribute("title"));
        Assert.Single(cut.FindAll(".terminal-directory .copy-icon"));
        Assert.Empty(cut.FindAll("script, a"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            WorkingDirectory = "/a longer path/with spaces/and Unicode \u03bb",
        }));
        Assert.Equal("/a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory").GetAttribute("data-text"));
        Assert.Equal("/a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory-measure").TextContent);
        Assert.Equal("Copy working directory: /a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory").GetAttribute("aria-label"));
        Assert.Equal(button.Id, cut.Find(".terminal-directory").Id);
        cut.SetParametersAndRender(builder => builder.Add(p => p.State, new TerminalToolbarState()));
        Assert.Equal("shell", cut.Find(".terminal-title").TextContent);
        Assert.Empty(cut.FindAll(".terminal-directory"));
    }

    [Theory]
    [InlineData("/work/app")]
    [InlineData("C:\\src\\app")]
    [InlineData("ab\U0001F680cd")]
    [InlineData("abe\u0301cd")]
    [InlineData("/")]
    public void Directory_PreservesFullMeasurementAndCopyValue(string path)
    {
        var cut = RenderComponent<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            WorkingDirectory = path
        }));

        Assert.Equal(path, cut.Find(".terminal-metadata").GetAttribute("data-directory"));
        Assert.Equal(path, cut.Find(".terminal-directory-measure").TextContent);
        Assert.Empty(cut.Find(".terminal-directory-display").TextContent);
        Assert.Equal(path, cut.Find(".terminal-directory").GetAttribute("data-text"));
    }

    [Theory]
    [InlineData("normal", 0, "0", "0%", "TerminalProgress")]
    [InlineData("normal", 10, "10", "10%", "TerminalProgress")]
    [InlineData("normal", 100, "100", "100%", "TerminalProgress")]
    [InlineData("indeterminate", null, null, "", "TerminalProgress")]
    [InlineData("error", 25, "25", "25%", "TerminalProgressError")]
    [InlineData("warning", 75, "75", "75%", "TerminalProgressWarning")]
    public void Progress_ExposesAccessibleStateAndValue(string state, int? percentage, string? expectedValue, string expectedText, string label)
    {
        var cut = RenderComponent<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Connected = true, ProgressState = state, ProgressPercentage = percentage
        }));
        var progress = cut.Find("[role=progressbar]");
        Assert.Equal(expectedValue, progress.GetAttribute("aria-valuenow"));
        Assert.Equal(Resources.TerminalStrings.ResourceManager.GetString(label), progress.GetAttribute("aria-label"));
        var indicator = cut.Find(".terminal-progress");
        Assert.Equal(state, indicator.GetAttribute("data-state"));
        Assert.Equal(Resources.TerminalStrings.ResourceManager.GetString(label), indicator.GetAttribute("title"));
        Assert.Equal(expectedText, indicator.TextContent.Trim());
        Assert.Equal("terminal-progress", cut.Find(".terminal-metadata").Children[0].ClassName);
        Assert.Equal("terminal-title", cut.Find(".terminal-metadata").Children[1].ClassName);
        if (state == "indeterminate")
        {
            Assert.Empty(cut.FindAll(".terminal-progress-percentage"));
        }
        else
        {
            Assert.Single(cut.FindAll(".terminal-progress-percentage"));
        }
    }

    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "normal")]
    [InlineData(false, "indeterminate")]
    public void InactiveProgress_IsHidden(bool connected, string state)
    {
        var cut = RenderComponent<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Connected = connected, ProgressState = state, ProgressPercentage = 42
        }));
        Assert.Empty(cut.FindAll("[role=progressbar]"));
    }
}
