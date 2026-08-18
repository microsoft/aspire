// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Shared;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardDialogServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ShowDialog_MapsOverlayDismissBehavior(bool preventDismissOnOverlayClick, bool expectedModal)
    {
        var dialogService = CreateDialogService(out var innerDialogService);

        var reference = await dialogService.ShowDialogAsync<HelpDialog>(new DialogParameters
        {
            PreventDismissOnOverlayClick = preventDismissOnOverlayClick
        });

        Assert.Equal(expectedModal, innerDialogService.LastInstance!.Options.Modal);
        await reference.CloseAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowPanel_PreservesModalBehavior(bool modal)
    {
        var dialogService = CreateDialogService(out var innerDialogService);

        var reference = await dialogService.ShowPanelAsync<HelpDialog>(new DialogParameters
        {
            Modal = modal
        });

        Assert.Equal(modal, innerDialogService.LastInstance!.Options.Modal);
        await reference.CloseAsync();
    }

    private static DashboardDialogService CreateDialogService(out TestDialogService innerDialogService)
    {
        innerDialogService = new TestDialogService();
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));

        return new DashboardDialogService(
            innerDialogService,
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            dimensionManager);
    }
}