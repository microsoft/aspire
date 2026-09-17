// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using Assert = Xunit.Assert;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class TerminalSetupHelpers
{
    public static void SetupTerminalComponents(TestContext context, TestDashboardClient client, string pathBase = "")
    {
        FluentUISetupHelpers.AddCommonDashboardServices(context);
        FluentUISetupHelpers.SetupFluentUIComponents(context);
        FluentUISetupHelpers.SetupFluentButton(context);
        context.Services.AddSingleton<IDashboardClient>(client);
        context.JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle", _ => true).SetResult(string.Empty);
        SetupTerminalView(context, pathBase);
        SetupTerminalDock(context, pathBase);
    }

    public static void SetupTerminalView(TestContext context, string pathBase = "")
    {
        var module = SetupTerminalViewModule(context, $"{pathBase}/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        module.SetupVoid("setReadOnly", _ => true).SetVoidResult();
    }

    public static BunitJSModuleInterop SetupTerminalViewModule(TestContext context, string modulePath)
    {
        context.Services.TryAddSingleton<TerminalViewSessionRegistry>();
        FluentUISetupHelpers.SetupFluentList(context);
        var module = context.JSInterop.SetupModule(modulePath);
        module.Setup<int>("reconnectTerminal", _ => true).SetResult(2);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();
        module.SetupVoid("dismissError", _ => true).SetVoidResult();
        module.SetupVoid("refreshLayout", _ => true).SetVoidResult();
        module.SetupVoid("setAutoFit", _ => true).SetVoidResult();
        module.SetupVoid("fitToContainer", _ => true).SetVoidResult();
        module.Setup<TerminalSizePreset[]>("getSizePresets").SetResult(
            [new("80x24", "80×24", 80, 24)]);
        return module;
    }

    public static void SetupTerminalDock(TestContext context, string pathBase = "")
    {
        var dock = context.JSInterop.SetupModule("./Components/Layout/TerminalDock.razor.js");
        dock.SetupVoid("registerResizeHandle", _ => true).SetVoidResult();
        dock.SetupVoid("unregisterResizeHandle", _ => true).SetVoidResult();
        dock.SetupVoid("registerTabNavigation", _ => true).SetVoidResult();
        dock.SetupVoid("unregisterTabNavigation", _ => true).SetVoidResult();

        SetupTerminalWindows(context, pathBase);
    }

    public static BunitJSModuleInterop SetupTerminalWindows(TestContext context, string pathBase = "")
    {
        var windows = context.JSInterop.SetupModule($"{pathBase}/js/app-terminalwindow.js");
        windows.SetupVoid("registerTerminalWindowButton", _ => true).SetVoidResult();
        windows.Setup<bool>("focusTerminalWindow", _ => true).SetResult(true);
        windows.SetupVoid("closeTerminalWindow", _ => true).SetVoidResult();
        windows.SetupVoid("unregisterTerminalWindowButton", _ => true).SetVoidResult();
        return windows;
    }

    public static TerminalWindowLauncher GetWindowLauncher(TestContext context, IRenderedFragment component)
    {
        // A terminal-watch update can render a child before that child's OnAfterRenderAsync registers its listener.
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "registerTerminalWindowButton"));
        var registration = Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "registerTerminalWindowButton");
        return Assert.IsType<DotNetObjectReference<TerminalWindowLauncher>>(registration.Arguments[2]).Value;
    }

    public static void AssertSingleTerminalConnection(TestContext context, string expectedWebSocketUrl)
    {
        var invocation = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "initTerminal");
        var options = Assert.IsType<TerminalViewOptions>(invocation.Arguments[3]);
        Assert.Equal($"{expectedWebSocketUrl}&viewId={options.ViewId}", invocation.Arguments[1]);
        Assert.True(context.Services.GetRequiredService<TerminalViewSessionRegistry>().TryGet(
            options.ViewId, new Uri(expectedWebSocketUrl).PathAndQuery, out _));
    }

    public static WatchTerminalsUpdate Snapshot(params string[] terminalIds) => new()
    {
        Snapshot = new TerminalDescriptorList
        {
            Terminals = { terminalIds.Select(id => new TerminalDescriptor { TerminalId = id, Title = id }) }
        }
    };

    public static WatchTerminalsUpdate Change(TerminalChangeType changeType, string terminalId, string? title = null) => new()
    {
        Change = new TerminalChangeNotification
        {
            ChangeType = changeType,
            Terminal = new TerminalDescriptor { TerminalId = terminalId, Title = title ?? terminalId }
        }
    };
}
