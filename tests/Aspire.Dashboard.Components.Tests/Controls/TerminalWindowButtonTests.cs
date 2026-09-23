// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
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

    [Theory]
    [InlineData(true, "terminal", "terminal-window/apphost/terminal", 19)]
    [InlineData(false, null, "terminal-window/apphost/terminal", 19)]
    [InlineData(false, "", "terminal-window/apphost/terminal", 19)]
    [InlineData(false, "terminal", null, 19)]
    [InlineData(false, "terminal", "", 19)]
    [InlineData(false, "terminal", "terminal-window/apphost/terminal", null)]
    [InlineData(false, "terminal", "terminal-window/apphost/terminal", 0)]
    public async Task Registration_WaitsUntilLaunchingIsAvailable(bool disabled, string? key, string? url, int? fontSize)
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var registration = module.SetupVoid("registerTerminalWindowButton", _ => true);
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.Disabled, disabled)
            .Add(p => p.TerminalKey, key)
            .Add(p => p.Url, url)
            .Add(p => p.FontSize, fontSize));
        cut.Render();

        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        Assert.Equal([], JSInterop.Invocations.Where(invocation => invocation.Identifier == "import" &&
            Equals(invocation.Arguments[0], "/js/app-terminalwindow.js")));
        Assert.Empty(module.Invocations);

        cut.SetParametersAndRender(builder => builder
            .Add(p => p.Disabled, false)
            .Add(p => p.TerminalKey, "terminal")
            .Add(p => p.Url, "terminal-window/apphost/terminal")
            .Add(p => p.FontSize, 19));
        Assert.Single(registration.Invocations);
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        cut.Render();
        Assert.Single(registration.Invocations);
        registration.SetVoidResult();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<FluentButton>().Instance.Disabled));

        cut.SetParametersAndRender(builder => builder.Add(p => p.Disabled, true));
        cut.SetParametersAndRender(builder => builder.Add(p => p.Disabled, false));
        Assert.Single(registration.Invocations);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "unregisterTerminalWindowButton");
    }

    [Fact]
    public async Task Disposal_BeforeLaunchingIsAvailable_DoesNotLoadJavaScript()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var cut = RenderComponent<TerminalWindowButton>(builder => builder.Add(p => p.Label, "Open"));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        Assert.Empty(module.Invocations);
        Assert.Equal([], JSInterop.Invocations.Where(invocation => invocation.Identifier == "import" &&
            Equals(invocation.Arguments[0], "/js/app-terminalwindow.js")));
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
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.TerminalKey, "terminal")
            .Add(p => p.Url, "terminal-window/apphost/terminal")
            .Add(p => p.FontSize, 19));
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
        cut.Render();
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "registerTerminalWindowButton");
        Assert.Single(toasts.FindComponents<FluentToast>());
    }

    [Fact]
    public async Task Adoption_RegistersWithoutViewerMetadataAndWaitsForReconciliation()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var registration = module.SetupVoid("registerTerminalWindowButton", _ => true);
        var adoption = module.SetupVoid("adoptTerminalWindows", _ => true);
        var opened = new List<(string Key, TerminalWindowOpenResult Result)>();
        var checkedKeys = Channel.CreateUnbounded<string[]>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.Disabled, true)
            .Add(p => p.WindowKeysToAdopt, ["first", "inactive"])
            .Add(p => p.OnWindowOpened, launch => opened.Add(launch))
            .Add(p => p.OnWindowsAdopted, keys => { checkedKeys.Writer.TryWrite(keys); }));

        Assert.Single(registration.Invocations);
        Assert.Empty(adoption.Invocations);
        Assert.False(checkedKeys.Reader.TryPeek(out _));
        registration.SetVoidResult();

        // Registration renders before starting adoption, which is deliberately paused here. No subsequent render
        // can wake WaitForAssertion, so observe the interop invocation independently on the renderer instead.
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => cut.InvokeAsync(() => adoption.Invocations.Count > 0),
            "Window adoption did not start after registration completed.");
        var invocation = Assert.Single(adoption.Invocations);
        Assert.Equal(registration.Invocations.Single().Arguments[1], invocation.Arguments[0]);
        Assert.Equal(["first", "inactive"], Assert.IsType<string[]>(invocation.Arguments[1]));
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
        cut.Render();
        Assert.Single(registration.Invocations);
        Assert.Single(adoption.Invocations);

        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("inactive", "adopted"));
        Assert.Equal([("inactive", TerminalWindowOpenResult.Adopted)], opened);
        Assert.False(checkedKeys.Reader.TryPeek(out _));
        adoption.SetVoidResult();
        Assert.Equal(["first", "inactive"], await checkedKeys.Reader.ReadAsync().AsTask().DefaultTimeout());

        cut.SetParametersAndRender(builder => builder.Add(p => p.WindowKeysToAdopt, ["inactive", "new"]));
        Assert.Equal(["new"], await checkedKeys.Reader.ReadAsync().AsTask().DefaultTimeout());
        Assert.Equal(2, adoption.Invocations.Count);
        Assert.Equal(["new"], Assert.IsType<string[]>(adoption.Invocations.Last().Arguments[1]));
        Assert.Single(registration.Invocations);
        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
    }

    [Fact]
    public async Task Disposal_DuringAdoption_DoesNotSignalReadinessOrCloseWindows()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var adoption = module.SetupVoid("adoptTerminalWindows", _ => true);
        var checkedKeys = new List<string[]>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.WindowKeysToAdopt, ["terminal"])
            .Add(p => p.OnWindowsAdopted, keys => checkedKeys.Add(keys)));
        Assert.Single(adoption.Invocations);

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        adoption.SetVoidResult();
        cut.Render();

        Assert.Empty(checkedKeys);
        Assert.Equal(["registerTerminalWindowButton", "adoptTerminalWindows", "unregisterTerminalWindowButton"],
            module.Invocations.Select(i => i.Identifier));
    }

    [Fact]
    public void AdoptionFailure_DoesNotSignalReadinessOrRetryOnEveryRender()
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var adoption = module.SetupVoid("adoptTerminalWindows", _ => true);
        adoption.SetException(new JSException("Window reconciliation failed"));
        var checkedKeys = new List<string[]>();
        var toasts = RenderComponent<FluentToastProvider>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.WindowKeysToAdopt, ["terminal"])
            .Add(p => p.OnWindowsAdopted, keys => checkedKeys.Add(keys)));

        toasts.WaitForAssertion(() => Assert.Equal(Resources.TerminalStrings.TerminalWindowTrackingFailed,
            Assert.Single(toasts.FindComponents<FluentToast>()).Instance.Title));
        cut.Render();
        Assert.Empty(checkedKeys);
        Assert.Single(adoption.Invocations);
    }

    [Theory]
    [InlineData("registerTerminalWindowButton")]
    [InlineData("adoptTerminalWindows")]
    public async Task TrackingFailure_ReportsNewTerminalsWithoutRetrying(string operation)
    {
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var failure = module.SetupVoid(operation, _ => true);
        failure.SetException(new JSException("Browser storage unavailable"));
        var failedKeys = Channel.CreateUnbounded<string[]>();
        var checkedKeys = new List<string[]>();
        RenderComponent<FluentToastProvider>();
        var cut = RenderComponent<TerminalWindowButton>(builder => builder
            .Add(p => p.Label, "Open")
            .Add(p => p.WindowKeysToAdopt, ["first"])
            .Add(p => p.OnWindowsAdopted, keys => checkedKeys.Add(keys))
            .Add(p => p.OnWindowTrackingFailed, keys => { failedKeys.Writer.TryWrite(keys); }));
        Assert.Equal(["first"], await failedKeys.Reader.ReadAsync().AsTask().DefaultTimeout());

        cut.SetParametersAndRender(builder => builder.Add(p => p.WindowKeysToAdopt, ["first", "new"]));
        Assert.Equal(["new"], await failedKeys.Reader.ReadAsync().AsTask().DefaultTimeout());
        cut.Render();
        Assert.False(failedKeys.Reader.TryPeek(out _));
        Assert.Empty(checkedKeys);
        Assert.Single(failure.Invocations);
    }
}
