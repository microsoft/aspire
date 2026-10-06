// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class TerminalsSetupHelpers
{
    public static void SetupPage(BunitContext context, TestDashboardClient client, ISessionStorage? sessionStorage = null, string pathBase = "")
    {
        FluentUISetupHelpers.AddCommonDashboardServices(context, sessionStorage: sessionStorage);
        FluentUISetupHelpers.SetupFluentUIComponents(context);
        FluentUISetupHelpers.SetupFluentDialogProvider(context);
        FluentUISetupHelpers.SetupFluentDivider(context);
        FluentUISetupHelpers.SetupFluentInputLabel(context);
        FluentUISetupHelpers.SetupFluentKeyCode(context);
        FluentUISetupHelpers.SetupFluentMenu(context);
        FluentUISetupHelpers.SetupFluentAnchor(context);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(context);
        FluentUISetupHelpers.SetupFluentToolbar(context);
        FluentUISetupHelpers.SetupFluentButton(context);
        context.Services.AddSingleton<IDashboardClient>(client);
        context.Services.AddSingleton<IconResolver>();
        context.Services.AddScoped<DashboardCommandExecutor>();
        context.JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle", _ => true).SetResult(string.Empty);
        TerminalSetupHelpers.SetupTerminalView(context, pathBase);
        TerminalSetupHelpers.SetupTerminalWindows(context, pathBase);
    }
}
