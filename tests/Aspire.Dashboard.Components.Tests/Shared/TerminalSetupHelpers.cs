// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class TerminalSetupHelpers
{
    public static void SetupTerminalComponents(TestContext context, TestDashboardClient client)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(context);
        FluentUISetupHelpers.SetupFluentUIComponents(context);
        FluentUISetupHelpers.SetupFluentButton(context);
        context.Services.AddSingleton<IDashboardClient>(client);
        context.JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle", _ => true).SetResult(string.Empty);
        SetupTerminalView(context);
        SetupTerminalDock(context);
    }

    public static void SetupTerminalView(TestContext context)
    {
        var module = SetupTerminalViewModule(context, "/Components/Controls/TerminalView.razor.js");
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
        module.SetupVoid("refreshLayout", _ => true).SetVoidResult();
        module.SetupVoid("setAutoFit", _ => true).SetVoidResult();
        module.SetupVoid("fitToContainer", _ => true).SetVoidResult();
        module.Setup<TerminalSizePreset[]>("getSizePresets").SetResult(
            [new("80x24", "80×24", 80, 24)]);
        return module;
    }

    public static void SetupTerminalDock(TestContext context)
    {
        var dock = context.JSInterop.SetupModule("./Components/Layout/TerminalDock.razor.js");
        dock.SetupVoid("registerResizeHandle", _ => true).SetVoidResult();
        dock.SetupVoid("unregisterResizeHandle", _ => true).SetVoidResult();
        dock.SetupVoid("registerTabNavigation", _ => true).SetVoidResult();
        dock.SetupVoid("unregisterTabNavigation", _ => true).SetVoidResult();

        var windows = context.JSInterop.SetupModule("/js/app-terminalwindow.js");
        windows.Setup<string>("openTerminalWindow", _ => true).SetResult("opened");
        windows.Setup<bool>("focusTerminalWindow", _ => true).SetResult(true);
        windows.SetupVoid("closeTerminalWindow", _ => true).SetVoidResult();
        windows.SetupVoid("untrackTerminalWindow", _ => true).SetVoidResult();
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
