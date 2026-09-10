// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Terminal;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
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
        FluentUISetupHelpers.SetupFluentButton(this);
    }

    [Fact]
    public void Initialization_ProvidesLocalizedCopyAndInputControlsWithoutHostSubscriber()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "shell")
            .Add(p => p.SizeMemoryKey, "dock:shell"));

        var button = cut.Find("div[hidden] .terminal-selection-copy");
        Assert.Equal(Resources.ControlsStrings.GridValueCopyToClipboard, button.GetAttribute("aria-label"));
        Assert.Equal(Resources.ControlsStrings.GridValueCopyToClipboard, button.GetAttribute("title"));
        Assert.Single(button.QuerySelectorAll("svg"));
        var invocation = Assert.Single(initialization.Invocations);
        Assert.IsType<DotNetObjectReference<TerminalView>>(invocation.Arguments[2]);
        var options = Assert.IsType<TerminalViewOptions>(invocation.Arguments[3]);
        Assert.Equal("dock:shell", options.SizeMemoryKey);
        Assert.Equal(Resources.ConsoleLogs.TerminalInputLabel, options.Label);
        Assert.IsType<ElementReference>(invocation.Arguments[4]);
        Assert.IsType<ElementReference>(invocation.Arguments[5]);
        var registry = Services.GetRequiredService<TerminalViewSessionRegistry>();
        Assert.True(registry.TryGet(options.ViewId, new Uri(Assert.IsType<string>(invocation.Arguments[1])).PathAndQuery, out _));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ChromeAndFooter_RespectSurfaceParameters(bool chromeless, bool showDimensions)
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "shell")
            .Add(p => p.Chromeless, chromeless)
            .Add(p => p.ShowDimensionsPicker, showDimensions));

        Assert.Equal(chromeless, cut.Find(".terminal-view").ClassList.Contains("terminal-chromeless"));
        Assert.Equal(chromeless ? 0 : 1, cut.FindAll(".terminal-titlebar").Count);
        Assert.Equal(showDimensions ? 1 : 0, cut.FindAll(".terminal-size-select").Count);
        Assert.Equal(showDimensions ? 1 : 0, cut.FindAll(".terminal-fit").Count);
        Assert.Single(cut.FindAll(".terminal-font-minus"));
        Assert.Single(cut.FindAll(".terminal-font-plus"));
        Assert.Equal(Resources.ConsoleLogs.TerminalFocusControlsHint, cut.Find(".terminal-focus-hint").TextContent);
        Assert.Equal(Resources.ConsoleLogs.TerminalToolbarDecreaseFontSize, cut.Find(".terminal-font-minus").GetAttribute("aria-label"));
        Assert.Equal(Resources.ConsoleLogs.TerminalToolbarIncreaseFontSize, cut.Find(".terminal-font-plus").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task InitialFontSize_SeedsMountWithoutResettingCurrentFont()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "shell")
            .Add(p => p.InitialFontSize, 19));
        Assert.Equal(19, cut.Instance.FontSize);
        var options = Assert.IsType<TerminalViewOptions>(
            Assert.Single(module.Invocations, i => i.Identifier == "initTerminal").Arguments[3]);
        Assert.Equal(19, options.InitialFontSize);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 21
        }));
        cut.SetParametersAndRender(builder => builder.Add(p => p.InitialFontSize, 17));
        Assert.Equal(21, cut.Instance.FontSize);
        Assert.Single(module.Invocations, i => i.Identifier == "initTerminal");
    }

    [Fact]
    public async Task FitButton_UsesCurrentStateAndKeepsThePickerForDimensionsOnly()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "shell"));
        var fitButton = cut.FindComponents<FluentButton>().Single(p => p.Instance.Class == "terminal-fit");
        Assert.True(fitButton.Instance.Disabled);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FitEnabled = true,
            Cols = 97, Rows = 38, SizeKey = "97x38", SizeSelectEnabled = true
        }));
        Assert.False(fitButton.Instance.Disabled);
        Assert.Equal(Resources.ConsoleLogs.TerminalToolbarGridSizeAuto, cut.Find(".terminal-fit").TextContent.Trim());
        var items = cut.FindComponent<FluentSelect<TerminalSizePreset, string>>().Instance.Items;
        Assert.NotNull(items);
        Assert.Equal([new("97x38", "97\u00d738", 97, 38), new TerminalSizePreset("80x24", "80\u00d724", 80, 24)], items);
        cut.Find(".terminal-fit").Click();
        Assert.Equal(new object?[] { 1 }, Assert.Single(module.Invocations, i => i.Identifier == "fitToContainer").Arguments);

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FitEnabled = false,
            Cols = 97, Rows = 38, SizeKey = "97x38"
        }));
        Assert.True(fitButton.Instance.Disabled);
    }

    [Fact]
    public void AutoFit_ChangesDuringInitializationApplyWithoutReconnecting()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.ResourceName, "shell").Add(p => p.AutoFit, true));
        Assert.True(Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).AutoFit);
        cut.SetParametersAndRender(builder => builder.Add(p => p.AutoFit, false));
        init.SetResult(1);
        cut.WaitForAssertion(() => Assert.Equal(new object?[] { 1, false },
            Assert.Single(module.Invocations, i => i.Identifier == "setAutoFit").Arguments));
        cut.SetParametersAndRender(builder => builder.Add(p => p.AutoFit, true));
        Assert.Equal(["initTerminal", "getSizePresets", "setAutoFit", "setAutoFit"], module.Invocations.Select(i => i.Identifier));
    }

    [Fact]
    public async Task TerminalChrome_UsesCurrentDimensionsAndIgnoresStaleCallbacks()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var initialization = module.Setup<int>("initTerminal", _ => true);
        initialization.SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "shell <worker>"));
        Assert.Equal("shell <worker>", cut.Find(".terminal-title").TextContent);
        Assert.Empty(cut.FindAll(".terminal-dimensions"));

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Cols = 120, Rows = 30, Connected = true
        }));
        Assert.Equal("120 \u00d7 30", cut.Find(".terminal-dimensions").TextContent);

        foreach (var state in new[]
        {
            new TerminalToolbarState { TerminalId = 1, Generation = 1, Cols = 80, Rows = 24 },
            new TerminalToolbarState { TerminalId = 2, Generation = 3, Cols = 80, Rows = 24 }
        })
        {
            await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(state));
            Assert.Equal("120 \u00d7 30", cut.Find(".terminal-dimensions").TextContent);
        }

        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Cols = 132, Rows = 50, Connected = true
        }));
        Assert.Equal("132 \u00d7 50", cut.Find(".terminal-dimensions").TextContent);
        Assert.Single(initialization.Invocations);
    }

    [Theory]
    [InlineData("mount-failed", nameof(Resources.ConsoleLogs.TerminalMountFailed))]
    [InlineData("disconnected", nameof(Resources.ConsoleLogs.TerminalDisconnected))]
    [InlineData("input-failed", nameof(Resources.ConsoleLogs.TerminalInputFailed))]
    [InlineData("sizing-failed", nameof(Resources.ConsoleLogs.TerminalSizingFailed))]
    public async Task TerminalError_DisplaysLocalizedAlert(string error, string resourceKey)
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Error = error
        }));
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();
        Assert.Equal(loc[resourceKey].Value, cut.Find("[role=alert]").TextContent);
        Assert.Single(cut.FindAll(".terminal-error fluent-button"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnly_InitialAndUpdatedStatePreservesConnection(bool initialReadOnly)
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        var update = module.SetupVoid("setReadOnly", _ => true);
        update.SetVoidResult();
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal")
            .Add(p => p.ReadOnly, initialReadOnly));
        Assert.Equal(initialReadOnly, Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).ReadOnly);
        var viewId = Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).ViewId;
        var registry = Services.GetRequiredService<TerminalViewSessionRegistry>();
        Assert.True(registry.TryGet(viewId, "/api/apphost-terminal?terminalId=terminal", out var session));
        Assert.Equal(initialReadOnly, session.ReadOnly);
        Assert.Empty(update.Invocations);

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, !initialReadOnly));
        Assert.Equal(!initialReadOnly, session.ReadOnly);
        cut.WaitForAssertion(() => Assert.Equal(new object?[] { 1, !initialReadOnly }, Assert.Single(update.Invocations).Arguments));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, initialReadOnly));
        Assert.Equal(initialReadOnly, session.ReadOnly);
        cut.WaitForAssertion(() => Assert.Collection(update.Invocations,
            invocation => Assert.Equal(new object?[] { 1, !initialReadOnly }, invocation.Arguments),
            invocation => Assert.Equal(new object?[] { 1, initialReadOnly }, invocation.Arguments)));
        Assert.Single(init.Invocations);
        Assert.Equal(["initTerminal", "getSizePresets", "setReadOnly", "setReadOnly"],
            module.Invocations.Select(i => i.Identifier));
    }

    [Fact]
    public void ReadOnly_ChangedDuringInitializationAndUpdateAppliesLatestValue()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var update = module.SetupVoid("setReadOnly", _ => true);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, true));
        var viewId = Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).ViewId;
        Assert.True(Services.GetRequiredService<TerminalViewSessionRegistry>().TryGet(
            viewId, "/api/apphost-terminal?terminalId=terminal", out var session));
        Assert.True(session.ReadOnly, "Authoritative policy must update before JS initialization returns");
        init.SetResult(1);
        cut.WaitForAssertion(() => Assert.Single(update.Invocations));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, false));
        update.SetVoidResult();
        cut.WaitForAssertion(() => Assert.Collection(update.Invocations,
            invocation => Assert.Equal(new object?[] { 1, true }, invocation.Arguments),
            invocation => Assert.Equal(new object?[] { 1, false }, invocation.Arguments)));
        Assert.Single(init.Invocations);
    }

    [Fact]
    public void ReadOnlyUpdateFailure_IsVisibleWithoutReconnect()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        module.SetupVoid("setReadOnly", _ => true).SetException(new JSException("Unsupported live input policy"));
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, true));
        cut.WaitForAssertion(() => Assert.Equal(Resources.ConsoleLogs.TerminalMountFailed, cut.Find("[role=alert]").TextContent));
        Assert.Equal(["initTerminal", "getSizePresets", "setReadOnly"], module.Invocations.Select(i => i.Identifier));
    }

    [Fact]
    public async Task InitializationFailure_OffersExplicitRetryWithoutRenderLoop()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var failed = true;
        var initialization = module.Setup<int>("initTerminal", _ => failed);
        initialization.SetException(new JSException("Worker unavailable"));
        var retry = module.Setup<int>("initTerminal", _ => !failed);
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        cut.WaitForAssertion(() => Assert.Equal(Resources.ConsoleLogs.TerminalMountFailed, cut.Find("[role=alert]").TextContent));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ResourceName, "app"));
        Assert.Single(initialization.Invocations);

        failed = false;
        var retrying = cut.Find(".terminal-error fluent-button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(retry.Invocations));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ResourceName, "app"));
        Assert.Single(retry.Invocations);
        retry.SetResult(1);
        await retrying;
    }

    [Theory]
    [InlineData("/api/apphost-terminal?terminalId=second")]
    [InlineData(null)]
    public void PendingInitialization_ReconcilesChangedEndpoint(string? updatedEndpoint)
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=first"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, updatedEndpoint));
        init.SetResult(1);
        cut.WaitForAssertion(() =>
        {
            Assert.Single(init.Invocations);
            if (updatedEndpoint is null)
            {
                Assert.Single(module.Invocations, i => i.Identifier == "disposeTerminal");
            }
            else
            {
                var reconnect = Assert.Single(module.Invocations, i => i.Identifier == "reconnectTerminal");
                AssertBoundEndpoint($"ws://localhost{updatedEndpoint}", reconnect.Arguments[1]);
            }
        });
    }

    [Theory]
    [InlineData("/api/apphost-terminal?terminalId=second")]
    [InlineData(null)]
    public async Task PendingExplicitRetry_ReconcilesChangedEndpoint(string? updatedEndpoint)
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var failed = true;
        module.Setup<int>("initTerminal", _ => failed).SetException(new JSException("Worker unavailable"));
        var retry = module.Setup<int>("initTerminal", _ => !failed);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=first"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=alert]")));
        failed = false;
        var retrying = cut.Find(".terminal-error fluent-button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(retry.Invocations));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, updatedEndpoint));
        retry.SetResult(1);
        await retrying;
        cut.WaitForAssertion(() =>
        {
            Assert.Single(retry.Invocations);
            if (updatedEndpoint is null)
            {
                Assert.Single(module.Invocations, i => i.Identifier == "disposeTerminal");
            }
            else
            {
                var reconnect = Assert.Single(module.Invocations, i => i.Identifier == "reconnectTerminal");
                AssertBoundEndpoint($"ws://localhost{updatedEndpoint}", reconnect.Arguments[1]);
            }
        });
    }

    [Fact]
    public async Task EndpointDetached_IgnoresLateStateWithoutRecreatingTheView()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=first"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, null));
        await cut.InvokeAsync(() => cut.Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 2, Error = "disconnected"
        }));
        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.Single(init.Invocations);
    }

    [Fact]
    public void EndpointRemovedAndRestored_DetachesAndInitializesAgain()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        var cut = RenderComponent<TerminalView>();
        Assert.Empty(init.Invocations);
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=first"));
        cut.WaitForAssertion(() => Assert.Single(init.Invocations));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, null));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, i => i.Identifier == "disposeTerminal"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=second"));
        cut.WaitForAssertion(() => Assert.Equal(2, init.Invocations.Count));
    }

    [Theory]
    [InlineData("http://localhost:8080/aspire/", "/aspire/Components/Controls/TerminalView.razor.js", "ws://localhost:8080/aspire/api/terminal?resource=app%20%26%20name&replica=2")]
    [InlineData("https://dashboard.example/nested/aspire/", "/nested/aspire/Components/Controls/TerminalView.razor.js", "wss://dashboard.example/nested/aspire/api/terminal?resource=app%20%26%20name&replica=2")]
    public void Initialization_PreservesPathBaseAndWebSocketScheme(string baseUri, string modulePath, string socketUrl)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager(baseUri));
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, modulePath);
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app & name").Add(p => p.ReplicaIndex, 2));
        var invocation = Assert.Single(init.Invocations);
        var options = Assert.IsType<TerminalViewOptions>(invocation.Arguments[3]);
        Assert.Equal($"{socketUrl}&viewId={options.ViewId}", invocation.Arguments[1]);
    }

    [Theory]
    [InlineData("api/apphost-terminal?terminalId=terminal", "/aspire/api/apphost-terminal?terminalId=terminal")]
    [InlineData("/api/apphost-terminal?terminalId=terminal", "/api/apphost-terminal?terminalId=terminal")]
    public void ExplicitEndpoint_RegistersTheResolvedPathAndQuery(string endpoint, string expectedPathAndQuery)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager("https://dashboard.example/aspire/"));
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/aspire/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        RenderComponent<TerminalView>(builder => builder.Add(p => p.EndpointPathAndQuery, endpoint));
        var invocation = Assert.Single(init.Invocations);
        var options = Assert.IsType<TerminalViewOptions>(invocation.Arguments[3]);
        Assert.Equal($"wss://dashboard.example{expectedPathAndQuery}&viewId={options.ViewId}", invocation.Arguments[1]);
        Assert.True(Services.GetRequiredService<TerminalViewSessionRegistry>().TryGet(
            options.ViewId, expectedPathAndQuery, out _));
    }

    [Fact]
    public async Task DisposalDuringInitialization_DisposesReturnedTerminalExactlyOnce()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.ResourceName, "app"));
        cut.WaitForAssertion(() => Assert.Single(init.Invocations));
        var disposing = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.False(disposing.IsCompleted);
        init.SetResult(1);
        await disposing;
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        Assert.Equal(1, Assert.Single(module.Invocations, i => i.Identifier == "disposeTerminal").Arguments[0]);
    }

    [Fact]
    public void EndpointChange_RotatesRegistrationAndDisablesOldInput()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=first"));
        var firstId = Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).ViewId;
        var registry = Services.GetRequiredService<TerminalViewSessionRegistry>();
        Assert.True(registry.TryGet(firstId, "/api/apphost-terminal?terminalId=first", out var firstSession));

        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=second"));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, i => i.Identifier == "reconnectTerminal"));
        var secondUrl = new Uri(Assert.IsType<string>(Assert.Single(module.Invocations, i => i.Identifier == "reconnectTerminal").Arguments[1]));
        var secondId = QueryHelpers.ParseQuery(secondUrl.Query)["viewId"].ToString();
        Assert.NotEqual(firstId, secondId);
        Assert.False(registry.TryGet(firstId, "/api/apphost-terminal?terminalId=first", out _));
        Assert.True(firstSession.ReadOnly);
        Assert.True(registry.TryGet(secondId, "/api/apphost-terminal?terminalId=second", out _));
    }

    [Fact]
    public void CompletionBeforeInitializationFinishes_DisablesInputWithoutDismissingTheView()
    {
        var module = TerminalSetupHelpers.SetupTerminalViewModule(this, "/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal"));
        var viewId = Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]).ViewId;
        Assert.True(Services.GetRequiredService<TerminalViewSessionRegistry>().TryGet(
            viewId, "/api/apphost-terminal?terminalId=terminal", out var session));
        Assert.False(session.Ended.IsCompleted);
        session.MarkEnded();
        Assert.True(session.Ended.IsCompletedSuccessfully);
        Assert.True(session.ReadOnly);
        Assert.Single(cut.FindAll(".terminal-container"));
        Assert.Equal(["initTerminal"], module.Invocations.Select(i => i.Identifier));
        init.SetResult(1);
        cut.WaitForAssertion(() => Assert.Single(init.Invocations));
        Assert.True(session.ReadOnly);
    }

    private static void AssertBoundEndpoint(string expected, object? value)
    {
        var actual = Assert.IsType<string>(value);
        var viewId = QueryHelpers.ParseQuery(new Uri(actual).Query)["viewId"].ToString();
        Assert.True(Guid.TryParseExact(viewId, "N", out _));
        Assert.Equal($"{expected}&viewId={viewId}", actual);
    }
}
