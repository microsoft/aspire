// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public sealed partial class ApplicationName : ComponentBase, IDisposable
{
    private CancellationTokenSource? _disposalCts;

    [Parameter]
    public string? AdditionalText { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    [Parameter]
    public IStringLocalizer? Loc { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    private string? _pageTitle;

    protected override async Task OnInitializedAsync()
    {
        DashboardClient.ConnectionStateChanged += OnConnectionStateChanged;

        // Wait for the client to connect, but proceed after 2 seconds regardless so the
        // page title is set even when the app host is unreachable.
        if (DashboardClient.IsEnabled && !DashboardClient.WhenConnected.IsCompletedSuccessfully)
        {
            _disposalCts = new CancellationTokenSource();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                await DashboardClient.WhenConnected.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!_disposalCts.IsCancellationRequested)
            {
                // Timed out waiting for connection — proceed with whatever ApplicationName is available.
            }
        }
    }

    private async void OnConnectionStateChanged(DashboardConnectionState state)
    {
        if (state is not DashboardConnectionState.Connected)
        {
            return;
        }

        try
        {
            await InvokeAsync(() =>
            {
                UpdatePageTitle();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
            // Component disposed, ignore.
        }
    }

    protected override void OnParametersSet()
    {
        UpdatePageTitle();
    }

    private void UpdatePageTitle()
    {
        string applicationName;

        if (ResourceName is not null && Loc is not null)
        {
            applicationName = string.Format(CultureInfo.InvariantCulture, Loc[ResourceName], DashboardClient.ApplicationName);
        }
        else
        {
            applicationName = DashboardClient.ApplicationName;
        }

        _pageTitle = string.IsNullOrEmpty(AdditionalText)
            ? applicationName
            : $"{applicationName} ({AdditionalText})";
    }

    public void Dispose()
    {
        DashboardClient.ConnectionStateChanged -= OnConnectionStateChanged;
        _disposalCts?.Cancel();
        _disposalCts?.Dispose();
    }
}
