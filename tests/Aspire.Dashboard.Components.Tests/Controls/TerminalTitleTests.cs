// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
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
        var cut = Render<TerminalTitle>(builder => builder
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
        Assert.Equal(Resources.TerminalStrings.TerminalCopyFailed, button.GetAttribute("data-copyfailed"));
        Assert.Equal("polite", cut.Find(".terminal-directory-container [role=status]").GetAttribute("aria-live"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("Copy working directory: /work/<app>", button.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("fluent-tooltip"));
        Assert.False(button.HasAttribute("title"));
        Assert.Single(cut.FindAll(".terminal-directory .copy-icon"));
        Assert.Empty(cut.FindAll("script, a"));
        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            WorkingDirectory = "/a longer path/with spaces/and Unicode \u03bb",
        }));
        Assert.Equal("/a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory").GetAttribute("data-text"));
        Assert.Equal("/a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory-measure").TextContent);
        Assert.Equal("Copy working directory: /a longer path/with spaces/and Unicode \u03bb", cut.Find(".terminal-directory").GetAttribute("aria-label"));
        Assert.Equal(button.Id, cut.Find(".terminal-directory").Id);
        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState()));
        Assert.Equal("shell", cut.Find(".terminal-title").TextContent);
        Assert.Empty(cut.FindAll(".terminal-directory"));
    }

    [Fact]
    public void Title_CopiesFullLiveTitleAndFallbackWithIndependentFeedback()
    {
        var title = "build <worker> \u03bb with a long title";
        var cut = Render<TerminalTitle>(builder => builder
            .Add(p => p.FallbackTitle, "shell")
            .Add(p => p.State, new TerminalToolbarState
            {
                Title = title,
                WorkingDirectory = "/work/app"
            }));

        var button = cut.Find(".terminal-title-button");
        var id = button.Id;
        Assert.NotEqual(cut.Find(".terminal-directory").Id, id);
        Assert.Equal(title, cut.Find(".terminal-title").TextContent);
        Assert.Equal(title, button.GetAttribute("data-text"));
        Assert.Equal("true", button.GetAttribute("data-copybutton"));
        Assert.Equal($"Copy terminal title: {title}", button.GetAttribute("aria-label"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopied, button.GetAttribute("data-postcopy"));
        Assert.Equal(Resources.TerminalStrings.TerminalCopyFailed, button.GetAttribute("data-copyfailed"));
        Assert.Equal("polite", cut.Find(".terminal-title-container [role=status]").GetAttribute("aria-live"));
        Assert.Single(button.QuerySelectorAll(".copy-icon"));
        Assert.Single(button.QuerySelectorAll(".checkmark-icon"));
        Assert.False(button.HasAttribute("title"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("fluent-tooltip"));

        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState { Title = "updated" }));
        Assert.Equal("updated", cut.Find(".terminal-title-button").GetAttribute("data-text"));
        Assert.Equal(id, cut.Find(".terminal-title-button").Id);
        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState()));
        Assert.Equal("shell", cut.Find(".terminal-title-button").GetAttribute("data-text"));
        Assert.Equal("Copy terminal title: shell", cut.Find(".terminal-title-button").GetAttribute("aria-label"));
        cut.Render(builder => builder.Add(p => p.FallbackTitle, ""));
        Assert.Empty(cut.FindAll(".terminal-title-button"));
    }

    [Fact]
    public void PathObserverFailure_DoesNotPreventCopyControlsFromRendering()
    {
        var module = JSInterop.SetupModule("./Components/Controls/TerminalTitle.razor.js");
        module.SetupVoid("observePath", _ => true).SetException(new JSException("Canvas unavailable"));

        var cut = Render<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Title = "shell",
            WorkingDirectory = "/work/app"
        }));

        Assert.Equal("shell", cut.Find(".terminal-title").TextContent);
        Assert.Equal("/work/app", cut.Find(".terminal-directory").GetAttribute("data-text"));
        Assert.Single(module.Invocations, i => i.Identifier == "observePath");
    }

    [Theory]
    [InlineData("/work/app")]
    [InlineData("C:\\src\\app")]
    [InlineData("ab\U0001F680cd")]
    [InlineData("abe\u0301cd")]
    [InlineData("/")]
    public void Directory_PreservesFullMeasurementAndCopyValue(string path)
    {
        var cut = Render<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
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
        var cut = Render<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Title = "shell", Connected = true, ProgressState = state, ProgressPercentage = percentage
        }));
        var progress = cut.Find("[role=progressbar]");
        Assert.Equal(expectedValue, progress.GetAttribute("aria-valuenow"));
        Assert.Equal(Resources.TerminalStrings.ResourceManager.GetString(label), progress.GetAttribute("aria-label"));
        var indicator = cut.Find(".terminal-icon");
        Assert.Equal(state, indicator.GetAttribute("data-state"));
        Assert.Equal(expectedValue is null ? Resources.TerminalStrings.ResourceManager.GetString(label) : expectedText, indicator.GetAttribute("title"));
        Assert.Empty(indicator.TextContent.Trim());
        Assert.Equal("terminal-icon", cut.Find(".terminal-metadata").Children[0].ClassName);
        Assert.Equal("terminal-title-container", cut.Find(".terminal-metadata").Children[1].ClassName);
        Assert.Single(indicator.Children);
    }

    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "normal")]
    [InlineData(false, "indeterminate")]
    public void InactiveProgress_ShowsTerminalIcon(bool connected, string state)
    {
        var cut = Render<TerminalTitle>(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Connected = connected, ProgressState = state, ProgressPercentage = 42
        }));
        Assert.Empty(cut.FindAll("[role=progressbar]"));
        Assert.Equal("none", cut.Find(".terminal-icon").GetAttribute("data-state"));
        Assert.IsType<Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.WindowConsole>(cut.FindComponent<FluentIcon<Icon>>().Instance.Value);
    }

    [Fact]
    public void ResourceIcon_ReturnsAfterProgressAndDisconnect()
    {
        var icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size16.Database();
        var cut = Render<TerminalTitle>(builder => builder.Add(p => p.IdleIcon, icon));
        Assert.Same(icon, cut.FindComponent<FluentIcon<Icon>>().Instance.Value);

        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState
        {
            Connected = true, ProgressState = "normal", ProgressPercentage = 42
        }));
        Assert.Single(cut.FindAll("[role=progressbar]"));
        Assert.Equal("42%", cut.Find(".terminal-icon").GetAttribute("title"));
        Assert.Empty(cut.FindComponents<FluentIcon<Icon>>());

        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState { Connected = true, ProgressState = "none" }));
        Assert.Same(icon, cut.FindComponent<FluentIcon<Icon>>().Instance.Value);
        cut.Render(builder => builder.Add(p => p.State, new TerminalToolbarState { Connected = false, ProgressState = "normal", ProgressPercentage = 42 }));
        Assert.Same(icon, cut.FindComponent<FluentIcon<Icon>>().Instance.Value);
        Assert.Null(cut.Find(".terminal-icon").GetAttribute("title"));
    }
}
