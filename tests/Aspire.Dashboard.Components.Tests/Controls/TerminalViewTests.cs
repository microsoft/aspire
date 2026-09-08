// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class TerminalViewTests : DashboardTestContext
{
    public TerminalViewTests()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
    }

    [Fact]
    public void SelectionTemplate_ProvidesLocalizedFluentCopyControl()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetResult(1);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "shell"));
        var button = cut.Find("div[hidden] .terminal-selection-copy");
        Assert.Equal(Resources.ControlsStrings.GridValueCopyToClipboard, button.GetAttribute("aria-label"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopyToClipboard, button.GetAttribute("data-copy-label"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopied, button.GetAttribute("data-copied-label"));
        Assert.Equal(2, button.QuerySelectorAll("svg").Length);
        Assert.True(cut.Find("[data-copied-icon]").HasAttribute("hidden"));
        Assert.Equal("polite", cut.Find(".terminal-selection-status").GetAttribute("aria-live"));
        var invocation = Assert.Single(initialization.Invocations);
        Assert.IsType<ElementReference>(invocation.Arguments[4]);
    }

    [Fact]
    public async Task TerminalChrome_DisplaysResourceAndCurrentDimensionsWithoutRemounting()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetResult(1);
        var disposal = module.SetupVoid("disposeTerminal", _ => true);
        disposal.SetVoidResult();
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "shell <worker>"));
        Assert.Single(cut.FindAll(".terminal-frame > .terminal-body > .terminal-container"));

        Assert.Equal("shell <worker>", cut.Find(".terminal-titlebar .terminal-title").TextContent);
        Assert.Empty(cut.FindAll(".terminal-dimensions"));

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Cols = 120, Rows = 30, Connected = true
        }));
        Assert.Equal("120 \u00d7 30", cut.Find(".terminal-dimensions").TextContent);
        Assert.Equal(Resources.ConsoleLogs.TerminalToolbarGridSize, cut.Find(".terminal-dimensions").GetAttribute("title"));

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Cols = 80, Rows = 24
        }));
        Assert.Equal("120 \u00d7 30", cut.Find(".terminal-dimensions").TextContent);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Cols = 132, Rows = 50, Connected = true
        }));
        Assert.Equal("132 \u00d7 50", cut.Find(".terminal-dimensions").TextContent);
        Assert.Single(cut.FindAll(".terminal-frame > .terminal-body > .terminal-container"));
        Assert.Single(initialization.Invocations);
        Assert.Empty(disposal.Invocations);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 3
        }));
        Assert.Empty(cut.FindAll(".terminal-dimensions"));
        Assert.Equal("shell <worker>", cut.Find(".terminal-title").TextContent);
    }

    [Theory]
    [InlineData("mount-failed", nameof(Resources.ConsoleLogs.TerminalMountFailed))]
    [InlineData("disconnected", nameof(Resources.ConsoleLogs.TerminalDisconnected))]
    [InlineData("input-failed", nameof(Resources.ConsoleLogs.TerminalInputFailed))]
    [InlineData("sizing-failed", nameof(Resources.ConsoleLogs.TerminalSizingFailed))]
    public async Task TerminalError_DisplaysLocalizedAlert(string error, string resourceKey)
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1,
            Generation = 1,
            Error = error
        }));

        Assert.Equal(loc[resourceKey].Value, cut.Find("[role=alert]").TextContent);
        Assert.Single(cut.FindAll(".terminal-error fluent-button"));
    }

    [Fact]
    public void InitializationFailure_DisplaysErrorWithoutRetryLoop()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetException(new JSException("Worker module unavailable"));

        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(Resources.ConsoleLogs.TerminalMountFailed, cut.Find("[role=alert]").TextContent);
            Assert.Single(initialization.Invocations);
        });
    }

    [Theory]
    [InlineData("http://localhost:8080/aspire/", "/aspire/Components/Controls/TerminalView.razor.js", "ws://localhost:8080/aspire/api/terminal?resource=app%20%26%20name&replica=2")]
    [InlineData("https://dashboard.example/nested/aspire/", "/nested/aspire/Components/Controls/TerminalView.razor.js", "wss://dashboard.example/nested/aspire/api/terminal?resource=app%20%26%20name&replica=2")]
    public void Initialization_PreservesPathBaseAndWebSocketScheme(string baseUri, string modulePath, string socketUrl)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager(baseUri));
        var module = JSInterop.SetupModule(modulePath);
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetResult(1);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "app & name")
            .Add(p => p.ReplicaIndex, 2));

        cut.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(initialization.Invocations);
            Assert.Equal(socketUrl, invocation.Arguments[1]);
            Assert.Equal(Resources.ConsoleLogs.TerminalInputLabel, invocation.Arguments[3]);
        });
    }

    [Fact]
    public async Task Reconnect_IgnoresOldErrorAndClearsCurrentErrorAfterSuccess()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        module.Setup<int>("reconnectTerminal", _ => true).SetResult(2);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();
        var snapshots = new List<TerminalToolbarState>();
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "first")
            .Add(p => p.OnToolbarStateChanged, state => snapshots.Add(state)));

        await cut.InvokeAsync(() => cut.Instance.ReconnectAsync("second", 1));
        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Error = "mount-failed"
        }));
        Assert.Empty(snapshots);
        Assert.Empty(cut.FindAll("[role=alert]"));

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Error = "disconnected"
        }));
        Assert.Equal(Resources.ConsoleLogs.TerminalDisconnected, cut.Find("[role=alert]").TextContent);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Connected = true
        }));
        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.Collection(snapshots,
            state => Assert.Equal("disconnected", state.Error),
            state => Assert.True(state.Connected));
    }

    [Fact]
    public async Task DisposalDuringInitialization_DisposesReturnedTerminalExactlyOnce()
    {
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        var disposal = module.SetupVoid("disposeTerminal", _ => true);
        disposal.SetVoidResult();
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        cut.WaitForAssertion(() => Assert.Single(initialization.Invocations));

        var disposing = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.False(disposing.IsCompleted);
        initialization.SetResult(1);
        await disposing;
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        var invocation = Assert.Single(disposal.Invocations);
        Assert.Equal(1, invocation.Arguments[0]);
    }
}
