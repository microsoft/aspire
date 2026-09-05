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
/// The window is the terminal's whole viewport, so resizing it resizes the grid — that is the reason to detach in the
/// first place, and it comes for free from the chromeless fit layout plus the existing resize observer.
/// </para>
/// </remarks>
public sealed partial class TerminalWindow : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private string? _endpoint;
    private string _title = string.Empty;
    private bool _ended;
    private Task? _watchTask;

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

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Layout> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalWindow> Logger { get; init; }

    protected override void OnParametersSet()
    {
        if (TerminalId is { Length: > 0 } terminalId)
        {
            _endpoint = $"/api/apphost-terminal?terminalId={Uri.EscapeDataString(terminalId)}";

            // The title of an AppHost terminal is owned by the AppHost and can change while the window is open, and
            // the terminal can also be closed out from under it. Both arrive on the watch stream, so the window
            // follows it rather than showing a stale name or a dead grid.
            _title = terminalId;
            _watchTask ??= Task.Run(() => WatchTerminalsAsync(terminalId, _cts.Token), _cts.Token);
        }
        else if (ResourceName is { Length: > 0 } resourceName)
        {
            // Resource terminals are named by the resource, which does not change for the life of the window.
            _endpoint = null;
            _title = ReplicaIndex > 0 ? $"{resourceName} #{ReplicaIndex}" : resourceName;
        }
    }

    private async Task WatchTerminalsAsync(string terminalId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in DashboardClient.SubscribeTerminalsAsync(cancellationToken).ConfigureAwait(false))
            {
                var changed = update.KindCase switch
                {
                    WatchTerminalsUpdate.KindOneofCase.Snapshot => ApplySnapshot(terminalId, update.Snapshot),
                    WatchTerminalsUpdate.KindOneofCase.Change => ApplyChange(terminalId, update.Change),
                    _ => false
                };

                if (changed)
                {
                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            // A broken stream only costs the window its title updates; the terminal itself is on a separate socket.
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_watchTask is { } watchTask)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected. We cancelled _cts immediately above, so the watch task ends by design.
            }
        }

        _cts.Dispose();
    }
}
