// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Renders a single terminal as an entire browser window, with no dashboard chrome around it.
/// </summary>
/// <remarks>
/// <para>
/// This is what the dashboard opens when the user detaches a terminal. Because terminals are multi-headed, the window
/// is just another viewer: it reaches the dashboard on its own and keeps working after the page that spawned it is
/// reloaded or closed.
/// </para>
/// <para>
/// Opening the window requests primary once and fits the grid at the opener's selected font size. While primary,
/// the window resizes the grid to its viewport without changing that font size.
/// </para>
/// </remarks>
public sealed partial class TerminalWindow : ComponentBase, IAsyncDisposable
{
    private string? _endpoint;
    private string _title = string.Empty;
    private bool _ended;
    private bool _disposed;
    private (string? TerminalId, string? ResourceName, int ReplicaIndex, string? WindowOwner, string? WindowGeneration)? _routeIdentity;
    private int _watchGeneration;
    private CancellationTokenSource? _watchCts;
    // Also tracks in-flight cancellation so overlapping route changes and disposal join the same cleanup.
    private Task _watchTask = Task.CompletedTask;
    private IJSObjectReference? _windowModule;
    private DotNetObjectReference<TerminalWindow>? _windowReference;
    private Task? _windowRegistrationTask;
    private string? _windowRegistrationId;
    private bool _windowReady = true;
    private bool _windowTrackingFailed;

    /// <summary>
    /// Gets or sets the id of an AppHost-owned dock terminal to attach to.
    /// </summary>
    [Parameter]
    public string? TerminalId { get; set; }

    /// <summary>
    /// Gets or sets the name of the resource whose terminal to attach to.
    /// </summary>
    [Parameter]
    public string? ResourceName { get; set; }

    /// <summary>
    /// Gets or sets the 0-based replica index of the resource terminal to attach to.
    /// </summary>
    [Parameter]
    public int ReplicaIndex { get; set; }

    /// <summary>Gets or sets the font size carried from the terminal's originating surface.</summary>
    [SupplyParameterFromQuery(Name = "fontSize")]
    public int? FontSize { get; set; }

    /// <summary>Gets or sets the opener identity carried by a coordinated dock window.</summary>
    [SupplyParameterFromQuery(Name = "windowOwner")]
    public string? WindowOwner { get; set; }

    /// <summary>Gets or sets the detachment generation carried by a coordinated dock window.</summary>
    [SupplyParameterFromQuery(Name = "windowGeneration")]
    public string? WindowGeneration { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.TerminalStrings> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalWindow> Logger { get; init; }

    protected override async Task OnParametersSetAsync()
    {
        var terminalId = TerminalId is { Length: > 0 } ? TerminalId : null;
        var resourceName = terminalId is null && ResourceName is { Length: > 0 } ? ResourceName : null;
        var replicaIndex = resourceName is not null ? ReplicaIndex : 0;
        var routeIdentity = (terminalId, resourceName, replicaIndex, WindowOwner, WindowGeneration);
        if (_disposed || _routeIdentity == routeIdentity)
        {
            return;
        }

        _routeIdentity = routeIdentity;
        var generation = ++_watchGeneration;
        _ended = false;
        _windowTrackingFailed = false;
        _windowReady = terminalId is null || (WindowOwner is null && WindowGeneration is null);
        _endpoint = terminalId is not null ? $"api/apphost-terminal?terminalId={Uri.EscapeDataString(terminalId)}" : null;
        _title = terminalId ?? (resourceName is not null
            ? replicaIndex > 0 ? $"{resourceName} #{replicaIndex}" : resourceName
            : string.Empty);

        await StopWindowTrackingAsync(release: true);
        if (_disposed || generation != _watchGeneration)
        {
            return;
        }
        _windowRegistrationTask = null;
        await StopWatchingAsync();
        if (_disposed || generation != _watchGeneration || terminalId is null)
        {
            return;
        }

        // Only AppHost terminals need metadata updates. A newer route may have replaced this one while
        // cancellation was awaiting an old watch, so don't start a subscription until its identity is rechecked.
        _watchCts = new CancellationTokenSource();
        var cancellationToken = _watchCts.Token;
        _watchTask = Task.Run(() => WatchTerminalsAsync(terminalId, generation, cancellationToken), cancellationToken);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed)
        {
            return;
        }
        if (_ended)
        {
            await StopWindowTrackingAsync(release: true);
        }
        else if (!_windowReady && !_windowTrackingFailed && _windowRegistrationTask is null && TerminalId is { } terminalId)
        {
            _windowRegistrationTask = RegisterWindowAsync(terminalId, _watchGeneration);
            await _windowRegistrationTask;
        }
    }

    private async Task RegisterWindowAsync(string terminalId, int generation)
    {
        try
        {
            var moduleUri = new Uri(new Uri(NavigationManager.BaseUri), Assets["js/app-terminalwindow.js"]);
            _windowModule ??= await JS.InvokeAsync<IJSObjectReference>("import", moduleUri.PathAndQuery);
            if (_disposed || generation != _watchGeneration)
            {
                return;
            }
            _windowReference ??= DotNetObjectReference.Create(this);
            var id = _windowRegistrationId = Guid.NewGuid().ToString("N");
            var ready = await _windowModule.InvokeAsync<bool>("registerDetachedTerminalWindow",
                id, terminalId, NavigationManager.BaseUri, _windowReference);
            if (!_disposed && generation == _watchGeneration && !_windowTrackingFailed)
            {
                // A reload must check durable revocation before mounting an auto-fit viewer. Returning a window
                // while this document was loading must not let it take sizing control again.
                _windowReady = ready && !_ended;
                _ended |= !ready;
                StateHasChanged();
            }
        }
        catch (JSDisconnectedException)
        {
            // A new document will independently validate its generation.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to coordinate the detached terminal window.");
            if (!_disposed && generation == _watchGeneration)
            {
                _windowTrackingFailed = true;
                StateHasChanged();
            }
        }
    }

    /// <summary>Stops rendering a detached viewer after its generation was explicitly returned or replaced.</summary>
    /// <param name="id">The browser registration to revoke.</param>
    /// <returns>A task that completes after the viewer is removed.</returns>
    [JSInvokable]
    public Task OnDetachedTerminalWindowRevokedAsync(string id) => InvokeAsync(() =>
    {
        if (!_disposed && _windowRegistrationId == id)
        {
            _ended = true;
            _windowReady = false;
            StateHasChanged();
        }
    });

    /// <summary>Reports a browser coordination failure without treating it as a successfully recovered window.</summary>
    /// <param name="id">The affected browser registration.</param>
    /// <returns>A task that completes after the failure is displayed.</returns>
    [JSInvokable]
    public Task OnDetachedTerminalWindowTrackingFailedAsync(string id) => InvokeAsync(() =>
    {
        if (!_disposed && _windowRegistrationId == id)
        {
            _windowTrackingFailed = true;
            _windowReady = false;
            StateHasChanged();
        }
    });

    private async Task StopWindowTrackingAsync(bool release)
    {
        if (_windowRegistrationTask is { } registration)
        {
            await registration;
        }
        if (_windowModule is { } module && _windowRegistrationId is { } id)
        {
            _windowRegistrationId = null;
            try
            {
                await module.InvokeVoidAsync(release ? "releaseDetachedTerminalWindow" : "unregisterDetachedTerminalWindow", id);
            }
            catch (JSDisconnectedException)
            {
                // Disposal on document reload must not revoke the durable detachment.
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to release detached terminal window tracking.");
            }
        }
    }

    private async Task WatchTerminalsAsync(string terminalId, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in DashboardClient.SubscribeTerminalsAsync(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(() =>
                {
                    // An update can already be queued on the renderer when its subscription is cancelled.
                    // Compare generations, not just IDs: navigating away and back also replaces the watch.
                    if (_disposed || generation != _watchGeneration || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var changed = update.KindCase switch
                    {
                        WatchTerminalsUpdate.KindOneofCase.Snapshot => ApplySnapshot(terminalId, update.Snapshot),
                        WatchTerminalsUpdate.KindOneofCase.Change => ApplyChange(terminalId, update.Change),
                        _ => false
                    };

                    if (changed)
                    {
                        StateHasChanged();
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window is closing or has switched to another terminal.
        }
        catch (Exception ex)
        {
            // Transport failures are retried by the client. Log unexpected failures without failing the circuit.
            Logger.LogWarning(ex, "Terminal window watch stream ended unexpectedly.");
        }
    }

    private bool ApplySnapshot(string terminalId, TerminalDescriptorList snapshot)
    {
        var descriptor = snapshot.Terminals.FirstOrDefault(t => t.TerminalId == terminalId);
        if (descriptor is null)
        {
            // Detached windows can outlive the terminal they were opened for, including across a dashboard restart.
            return MarkEnded();
        }

        return SetTitle(descriptor.Title);
    }

    private bool ApplyChange(string terminalId, TerminalChangeNotification change)
    {
        if (change.Terminal.TerminalId != terminalId)
        {
            return false;
        }

        return change.ChangeType is TerminalChangeType.Removed
            ? MarkEnded()
            : SetTitle(change.Terminal.Title);
    }

    private bool SetTitle(string title)
    {
        if (string.IsNullOrEmpty(title) || _title == title)
        {
            return false;
        }

        _title = title;
        return true;
    }

    private bool MarkEnded()
    {
        if (_ended)
        {
            return false;
        }

        _ended = true;
        return true;
    }

    private Task StopWatchingAsync()
    {
        if (_watchCts is { } cts)
        {
            _watchCts = null;
            _watchTask = CancelWatchAsync(cts, _watchTask);
        }

        return _watchTask;
    }

    private static async Task CancelWatchAsync(CancellationTokenSource cts, Task watchTask)
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await watchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Task.Run can be cancelled before the watch delegate starts.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopWatchingAsync().ConfigureAwait(false);
        await StopWindowTrackingAsync(release: false).ConfigureAwait(false);
        if (_windowModule is { } module)
        {
            await Utils.JSInteropHelpers.SafeDisposeAsync(module).ConfigureAwait(false);
        }
        _windowReference?.Dispose();
    }
}
