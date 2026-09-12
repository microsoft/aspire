// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

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
    private (string? TerminalId, string? ResourceName, int ReplicaIndex)? _routeIdentity;
    private int _watchGeneration;
    private CancellationTokenSource? _watchCts;
    // Also tracks in-flight cancellation so overlapping route changes and disposal join the same cleanup.
    private Task _watchTask = Task.CompletedTask;

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

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Layout> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalWindow> Logger { get; init; }

    protected override async Task OnParametersSetAsync()
    {
        var terminalId = TerminalId is { Length: > 0 } ? TerminalId : null;
        var resourceName = terminalId is null && ResourceName is { Length: > 0 } ? ResourceName : null;
        var replicaIndex = resourceName is not null ? ReplicaIndex : 0;
        var routeIdentity = (terminalId, resourceName, replicaIndex);
        if (_disposed || _routeIdentity == routeIdentity)
        {
            return;
        }

        _routeIdentity = routeIdentity;
        var generation = ++_watchGeneration;
        _ended = false;
        _endpoint = terminalId is not null ? $"/api/apphost-terminal?terminalId={Uri.EscapeDataString(terminalId)}" : null;
        _title = terminalId ?? (resourceName is not null
            ? replicaIndex > 0 ? $"{resourceName} #{replicaIndex}" : resourceName
            : string.Empty);

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
    }
}
