// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardDialogServiceTests
{
    [Fact]
    public async Task WaitForData_AlreadyAvailable_DoesNotShowDialog()
    {
        var dialogService = CreateDialogService(out var innerDialogService);

        Assert.True(await WaitForDataAsync(_ => Task.FromResult(true), dialogService).DefaultTimeout());
        Assert.Null(innerDialogService.LastInstance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForData_Canceled_DoesNotNavigate(bool dismiss)
    {
        var dialogService = CreateDialogService(out var innerDialogService);
        var waitTask = WaitForDataAsync(_ => Task.FromResult(false), dialogService);
        var instance = Assert.IsType<DialogInstance>(innerDialogService.LastInstance);
        var content = Assert.IsType<InteractionsProgressDialogViewModel>(instance.Options.Parameters["Content"]);
        Assert.Equal("Waiting for trace data", content.Message);
        var loc = new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>();
        Assert.Equal(loc[nameof(Aspire.Dashboard.Resources.Dialogs.OpenSpanDialogCancelButtonText)].Value, instance.Options.Footer.PrimaryAction.Label);
        Assert.False(instance.Options.Footer.PrimaryAction.Visible);
        Assert.Null(instance.Options.Footer.SecondaryAction.Label);

        await instance.CloseAsync(dismiss ? DialogResult.Cancel() : DialogResult.Ok("cancel"));

        Assert.False(await waitTask.DefaultTimeout());
    }

    [Fact]
    public async Task WaitForData_BecomesAvailable_ClosesProgressDialog()
    {
        var dialogService = CreateDialogService(out var innerDialogService);
        var dataAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var waitTask = WaitForDataAsync(
            _ => Interlocked.Increment(ref callbackCount) == 1 ? Task.FromResult(false) : dataAvailable.Task,
            dialogService);

        Assert.NotNull(innerDialogService.LastInstance);
        dataAvailable.SetResult(true);

        Assert.True(await waitTask.DefaultTimeout());
        var result = await innerDialogService.LastInstance.Result.DefaultTimeout();
        Assert.False(result.Cancelled);
        Assert.Equal(true, result.Value);
    }

    private static Task<bool> WaitForDataAsync(Func<CancellationToken, Task<bool>> isAvailable, DashboardDialogService dialogService)
    {
        return TraceLinkHelpers.WaitForDataToBeAvailableAsync(
            isAvailable,
            "Waiting for trace data",
            dialogService,
            callback => callback(),
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAsync_WaitsForClosingAndResultCallbacks(bool withResult)
    {
        var dialogService = CreateDialogService(out _);
        var closingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowClosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reference = await dialogService.ShowDialogAsync<HelpDialog>(new DialogParameters
        {
            OnDialogClosing = EventCallback.Factory.Create<IDialogInstance>(this, async _ =>
            {
                closingStarted.SetResult();
                await allowClosing.Task;
            }),
            OnDialogResult = EventCallback.Factory.Create<DialogResult>(this, async _ =>
            {
                resultStarted.SetResult();
                await allowResult.Task;
            })
        });

        var closeTask = withResult ? reference.CloseAsync(DialogResult.Ok(true)) : reference.CloseAsync();
        try
        {
            await closingStarted.Task.DefaultTimeout();
            Assert.False(closeTask.IsCompleted);

            allowClosing.SetResult();
            await resultStarted.Task.DefaultTimeout();
            Assert.False(closeTask.IsCompleted);

            allowResult.SetResult();
            await closeTask.DefaultTimeout();
            Assert.True(reference.Result.IsCompletedSuccessfully);
            Assert.Equal(withResult ? true : null, (await reference.Result).Value);
        }
        finally
        {
            allowClosing.TrySetResult();
            allowResult.TrySetResult();
        }
    }

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