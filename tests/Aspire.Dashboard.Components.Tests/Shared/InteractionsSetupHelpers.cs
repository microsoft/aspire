// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Assert = Xunit.Assert;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class InteractionsSetupHelpers
{
    public static Func<IRenderedFragment> SetupDialog<TDialog, TContent>(
        TestContext context, Expression<Func<TDialog, TContent>> contentParameter, out DashboardDialogService dialogService)
        where TDialog : ComponentBase
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(context);
        FluentUISetupHelpers.SetupFluentButton(context);

        IRenderedFragment? cut = null;
        TestDialogService? testDialogService = null;
        testDialogService = new TestDialogService((content, _) =>
        {
            cut = context.RenderComponent<CascadingValue<IDialogInstance>>(builder =>
            {
                builder.Add(p => p.Value, testDialogService!.LastInstance!);
                builder.AddChildContent<TDialog>(childBuilder =>
                {
                    childBuilder.Add(contentParameter, Assert.IsType<TContent>(content));
                });
            });
            return Task.CompletedTask;
        });
        context.Services.RemoveAll<IDialogService>();
        context.Services.AddSingleton<IDialogService>(testDialogService);

        dialogService = new DashboardDialogService(
            testDialogService,
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            context.Services.GetRequiredService<DimensionManager>());
        return () => cut ?? throw new InvalidOperationException("The dialog was not rendered.");
    }
}
