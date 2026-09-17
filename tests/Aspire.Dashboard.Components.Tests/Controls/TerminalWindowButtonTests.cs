// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class TerminalWindowButtonTests : DashboardTestContext
{
    public TerminalWindowButtonTests()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentButton(this);
    }

    [Fact]
    public void RegistrationAndMetadata_AreRequiredBeforeEnablingNativeButton()
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager("http://localhost/aspire/nested/"));
        var module = TerminalSetupHelpers.SetupTerminalWindows(this, "/aspire/nested");
        var registration = module.SetupVoid("registerTerminalWindowButton", _ => true);
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open in new window")
            .Add(p => p.TerminalKey, "first")
            .Add(p => p.Url, "terminal-window/apphost/first")
            .Add(p => p.FontSize, 19));

        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        Assert.False(cut.FindComponent<FluentButton>().Instance.OnClick.HasDelegate);
        cut.SetParametersAndRender(builder => builder
            .Add(p => p.TerminalKey, "second #1/?%+")
            .Add(p => p.Url, "terminal-window/apphost/second%20%231%2F%3F%25%2B")
            .Add(p => p.FontSize, 23));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        registration.SetVoidResult();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<FluentButton>().Instance.Disabled));
        Assert.Equal("second #1/?%+", cut.Find("fluent-button").GetAttribute("data-terminal-window-key"));
        Assert.Equal("http://localhost/aspire/nested/terminal-window/apphost/second%20%231%2F%3F%25%2B?fontSize=23",
            cut.Find("fluent-button").GetAttribute("data-terminal-window-url"));
        Assert.Single(registration.Invocations);

        cut.SetParametersAndRender(builder => builder.Add(p => p.FontSize, null));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        Assert.Null(cut.Find("fluent-button").GetAttribute("data-terminal-window-url"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.FontSize, 17).Add(p => p.Disabled, true));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        cut.SetParametersAndRender(builder => builder.Add(p => p.Disabled, false).Add(p => p.TerminalKey, null));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        cut.SetParametersAndRender(builder => builder.Add(p => p.TerminalKey, "second").Add(p => p.Url, null));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        cut.SetParametersAndRender(builder => builder.Add(p => p.Url, "terminal-window/apphost/second"));
        Assert.False(cut.FindComponent<FluentButton>().Instance.Disabled);
    }

    [Fact]
    public async Task Notifications_UseCapturedKeyAndAreIgnoredAfterDisposal()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var opened = new List<(string Key, TerminalWindowOpenResult Result)>();
        var closed = new List<string>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.TerminalKey, "first")
            .Add(p => p.Url, "terminal-window/apphost/first")
            .Add(p => p.FontSize, 19)
            .Add(p => p.OnWindowOpened, launch => opened.Add(launch))
            .Add(p => p.OnWindowClosed, key => closed.Add(key)));
        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        cut.SetParametersAndRender(builder => builder.Add(p => p.TerminalKey, "second"));
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("first", "opened"));
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("first", "focused"));
        await cut.InvokeAsync(() => launcher.OnTerminalWindowClosedAsync("first"));
        Assert.Equal([("first", TerminalWindowOpenResult.Opened), ("first", TerminalWindowOpenResult.Focused)], opened);
        Assert.Equal(["first"], closed);

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("second", "opened"));
        await cut.InvokeAsync(() => launcher.OnTerminalWindowClosedAsync("second"));
        Assert.Equal(2, opened.Count);
        Assert.Equal(["first"], closed);
        var registration = Assert.Single(module.Invocations, i => i.Identifier == "registerTerminalWindowButton");
        var unregister = Assert.Single(module.Invocations, i => i.Identifier == "unregisterTerminalWindowButton");
        Assert.Equal(registration.Arguments[1], unregister.Arguments[0]);
        Assert.Equal(["registerTerminalWindowButton", "unregisterTerminalWindowButton"],
            module.Invocations.Select(i => i.Identifier));
    }

    [Fact]
    public async Task DisposalDuringRegistration_UnregistersOnceWithoutEnablingButton()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var registration = module.SetupVoid("registerTerminalWindowButton", _ => true);
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.TerminalKey, "terminal")
            .Add(p => p.Url, "terminal-window/apphost/terminal")
            .Add(p => p.FontSize, 19));
        Assert.Single(registration.Invocations);
        var disposing = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.False(disposing.IsCompleted);
        registration.SetVoidResult();
        await disposing;
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        Assert.Single(module.Invocations, i => i.Identifier == "unregisterTerminalWindowButton");
    }

    [Theory]
    [InlineData("blocked", nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowBlocked))]
    [InlineData("failed", nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowFailed))]
    public async Task BrowserFailure_ShowsActionableToast(string result, string resourceKey)
    {
        TerminalSetupHelpers.SetupTerminalWindows(this);
        var toasts = RenderComponent<FluentToastProvider>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder.Add(p => p.Label, "Open"));
        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("terminal", result));
        var toast = Assert.Single(toasts.FindComponents<FluentToast>()).Instance;
        Assert.Equal(ToastIntent.Error, toast.Intent);
        Assert.Equal(resourceKey == nameof(Resources.TerminalStrings.TerminalToolbarOpenInWindowBlocked)
            ? Resources.TerminalStrings.TerminalToolbarOpenInWindowBlocked
            : Resources.TerminalStrings.TerminalToolbarOpenInWindowFailed, toast.Title);
    }

    [Fact]
    public void RegistrationFailure_StaysDisabledAndShowsActionableToast()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        module.SetupVoid("registerTerminalWindowButton", _ => true).SetException(new JSException("Module registration failed"));
        var toasts = RenderComponent<FluentToastProvider>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.TerminalKey, "terminal")
            .Add(p => p.Url, "terminal-window/apphost/terminal")
            .Add(p => p.FontSize, 19));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        toasts.WaitForAssertion(() => Assert.Equal(Resources.TerminalStrings.TerminalToolbarOpenInWindowFailed,
            Assert.Single(toasts.FindComponents<FluentToast>()).Instance.Title));
    }
}
