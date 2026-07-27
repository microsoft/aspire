// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

public sealed class ResourceServiceConnectionProvider : ComponentBase, IAsyncDisposable
{
    private const int MaxAttemptsBeforeShowingRetry = 5;

    private DotNetObjectReference<ResourceServiceConnectionProvider>? _dotNetRef;
    private int _disconnectedCount;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override void OnInitialized()
    {
        if (!DashboardClient.IsEnabled)
        {
            return;
        }

        DashboardClient.ConnectionStateChanged += OnConnectionStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && DashboardClient.IsEnabled)
        {
            // Register the .NET object reference so JS can call back for retry.
            _dotNetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("registerResourceServiceConnectionProvider", _dotNetRef);

            // Report the current state immediately in case we're already disconnected.
            // Don't report Connecting — that's normal startup. Only report Disconnected.
            if (DashboardClient.ConnectionState is DashboardConnectionState.Disconnected)
            {
                await UpdateModalStateAsync(DashboardConnectionState.Disconnected);
            }
        }
    }

    private async void OnConnectionStateChanged(DashboardConnectionState state)
    {
        try
        {
            await InvokeAsync(() => UpdateModalStateAsync(state));
        }
        catch (ObjectDisposedException)
        {
            // Component disposed, ignore.
        }
        catch (JSDisconnectedException)
        {
            // Circuit disconnected, JS interop is no longer available.
        }
    }

    private async Task UpdateModalStateAsync(DashboardConnectionState state)
    {
        if (state is DashboardConnectionState.Connected)
        {
            _disconnectedCount = 0;
            await JS.InvokeVoidAsync("updateResourceServiceConnectionState", "connected", false);
        }
        else if (state is DashboardConnectionState.Disconnected)
        {
            _disconnectedCount++;
            var showRetry = _disconnectedCount >= MaxAttemptsBeforeShowingRetry;
            await JS.InvokeVoidAsync("updateResourceServiceConnectionState", "disconnected", showRetry);
        }

        // Connecting state — don't update modal.
    }

    [JSInvokable]
    public async Task ReconnectFromJs()
    {
        await DashboardClient.ReconnectAsync();
    }

    public ValueTask DisposeAsync()
    {
        DashboardClient.ConnectionStateChanged -= OnConnectionStateChanged;
        _dotNetRef?.Dispose();
        return ValueTask.CompletedTask;
    }
}
