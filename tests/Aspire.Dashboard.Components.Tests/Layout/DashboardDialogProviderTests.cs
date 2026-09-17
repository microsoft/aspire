// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class DashboardDialogProviderTests : DashboardTestContext
{
    [Theory]
    [InlineData("/traces")]
    [InlineData("/?hiddenTypes=Container")]
    public async Task Navigation_CancelsDialogsAndDrawers(string destination)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var cut = RenderComponent<DashboardDialogProvider>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var closed = new List<string>();
        var first = await OpenAsync(cut, "first", drawer: false, OnStateChange);
        var second = await OpenAsync(cut, "second", drawer: true, OnStateChange);

        void OnStateChange(DialogEventArgs args)
        {
            if (args.State == DialogState.Closing)
            {
                closed.Add(args.Instance!.Id);
                navigation.NavigateTo(destination + "#closing");
            }
        }

        await cut.InvokeAsync(() => navigation.NavigateTo(destination));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<FluentDialog>());
            Assert.True(first.IsCompletedSuccessfully);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Equal(["second", "first"], closed);
        });
        Assert.True((await first).Cancelled);
        Assert.True((await second).Cancelled);

        var reopened = await OpenAsync(cut, "reopened", drawer: false, onStateChange: null);
        Assert.False(reopened.IsCompleted);
        await cut.InvokeAsync(() => navigation.NavigateTo("/metrics"));
        cut.WaitForAssertion(() => Assert.True(reopened.IsCompletedSuccessfully));
        Assert.True((await reopened).Cancelled);
    }

    [Fact]
    public async Task Navigation_CancelsConfirmation()
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var cut = RenderComponent<DashboardDialogProvider>();
        var service = Services.GetRequiredService<IDialogService>();
        Task<DialogResult> result = null!;
        await cut.InvokeAsync(() => { result = service.ShowConfirmationAsync("Confirm navigation test"); });
        var dialog = cut.FindComponent<FluentDialog>().Instance;
        await RaiseOpeningAsync(cut, dialog.Id!);

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/traces"));

        cut.WaitForAssertion(() => Assert.True(result.IsCompletedSuccessfully));
        Assert.True((await result).Cancelled);
        Assert.Empty(cut.FindComponents<FluentDialog>());
    }

    [Fact]
    public async Task DisposedProvider_UnsubscribesFromNavigation()
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var cut = RenderComponent<DashboardDialogProvider>();
        await cut.InvokeAsync(DisposeComponents);
        var plainProvider = RenderComponent<FluentDialogProvider>();
        var result = await OpenAsync(plainProvider, "after-dispose", drawer: false, onStateChange: null);

        await plainProvider.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/traces"));

        Assert.False(result.IsCompleted);
        var dialog = plainProvider.FindComponent<FluentDialog>().Instance.Instance!;
        await plainProvider.InvokeAsync(dialog.CloseAsync);
        Assert.True(result.IsCompletedSuccessfully);
    }

    private async Task<Task<DialogResult>> OpenAsync(IRenderedFragment cut, string id, bool drawer, Action<DialogEventArgs>? onStateChange)
    {
        var service = Services.GetRequiredService<IDialogService>();
        Task<DialogResult> result = null!;
        await cut.InvokeAsync(() =>
        {
            var options = new DialogOptions { Id = id, OnStateChange = onStateChange };
            result = drawer
                ? service.ShowDrawerAsync<FluentDialogBody>(options)
                : service.ShowDialogAsync<FluentDialogBody>(options);
        });
        await RaiseOpeningAsync(cut, id);

        return result;
    }

    private static Task RaiseOpeningAsync(IRenderedFragment cut, string id)
    {
        return cut.InvokeAsync(() => cut.Find($"#{id}").TriggerEvent("ondialogbeforetoggle", new DialogToggleEventArgs
        {
            Id = id,
            Type = "beforetoggle",
            OldState = "closed",
            NewState = "open"
        }));
    }
}