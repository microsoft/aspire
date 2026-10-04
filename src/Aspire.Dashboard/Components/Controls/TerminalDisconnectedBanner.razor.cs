// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Shows recovery actions without discarding the disconnected terminal's output.
/// </summary>
public sealed partial class TerminalDisconnectedBanner : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private Task? _watchTask;
    private bool _executing;
    private bool _disposed;
    private bool _collapsed;
    private bool _focusToggle;
    private FluentButton? _toggleButton;

    /// <summary>Gets or sets the exact resource instance that owns this terminal.</summary>
    [Parameter]
    public string? ResourceName { get; set; }

    /// <summary>Gets or sets whether resource commands are unavailable in this view.</summary>
    [Parameter]
    public bool ReadOnly { get; set; }

    /// <summary>Raised when the user closes the owning dock tab.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>Raised when the banner is collapsed or expanded over the retained output.</summary>
    [Parameter]
    public EventCallback<bool> OnCollapsedChanged { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required DashboardCommandExecutor CommandExecutor { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.TerminalStrings> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalDisconnectedBanner> Logger { get; init; }

    private ResourceViewModel? Resource => ResourceName is not null ? _resources.GetValueOrDefault(ResourceName) : null;

    private int? ExitCode => Resource is { } resource && resource.TryGetExitCode(out var exitCode) ? exitCode : null;

    private string Message => ExitCode is { } exitCode
        ? Loc[nameof(Resources.TerminalStrings.TerminalDisconnectedWithExitCode), exitCode].Value
        : Loc[nameof(Resources.TerminalStrings.TerminalDisconnectedMessage)].Value;

    private string ShowBannerLabel => ExitCode is { } exitCode
        ? Loc[nameof(Resources.TerminalStrings.TerminalShowDisconnectedBannerWithExitCode), exitCode].Value
        : Loc[nameof(Resources.TerminalStrings.TerminalShowDisconnectedBanner)].Value;

    private CommandViewModel? RestartCommand
    {
        get
        {
            if (ReadOnly || DashboardClient.IsReadOnly || Resource is not { } resource)
            {
                return null;
            }

            // Finished resources commonly expose only Start. Use the advertised
            // command rather than synthesizing Restart, which the server may reject.
            var commands = resource.Commands.Where(c => c.State != CommandViewModelState.Hidden &&
                c.Name is CommandViewModel.RestartCommand or CommandViewModel.StartCommand);
            return commands.OrderBy(c => c.State != CommandViewModelState.Enabled)
                .ThenBy(c => c.Name != CommandViewModel.RestartCommand).FirstOrDefault();
        }
    }

    private bool RestartDisabled => _executing || RestartCommand is not { State: CommandViewModelState.Enabled } command ||
        Resource is not { } resource || CommandExecutor.IsExecuting(resource.Name, command.Name);

    protected override void OnInitialized()
    {
        if (ResourceName is not null && DashboardClient.IsEnabled)
        {
            _watchTask = WatchResourcesAsync(_cts.Token);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusToggle && _toggleButton is not null)
        {
            _focusToggle = false;
            await _toggleButton.Element.FocusAsync(preventScroll: true);
        }
    }

    private async Task SetCollapsedAsync(bool collapsed)
    {
        _collapsed = collapsed;
        _focusToggle = true;
        await OnCollapsedChanged.InvokeAsync(collapsed);
    }

    private async Task WatchResourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DashboardClient.WhenResourcesReady.WaitAsync(cancellationToken);
            var (snapshot, updates) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
            await InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                foreach (var resource in snapshot)
                {
                    _resources[resource.Name] = resource;
                }
                StateHasChanged();
            });
            await foreach (var changes in updates.WithCancellation(cancellationToken))
            {
                await InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resources.Remove(resource.Name);
                        }
                        else
                        {
                            _resources[resource.Name] = resource;
                        }
                    }
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to watch the disconnected terminal resource.");
            await DispatchExceptionAsync(ex);
        }
    }

    private async Task RestartResourceAsync()
    {
        if (RestartDisabled || Resource is not { } resource || RestartCommand is not { } command)
        {
            Logger.LogDebug("The disconnected terminal's restart command is no longer available.");
            return;
        }

        _executing = true;
        try
        {
            await CommandExecutor.ExecuteAsync(resource, command, r => ResourceViewModel.GetResourceName(r, _resources));
        }
        finally
        {
            _executing = false;
        }
    }

    private async Task CloseAsync()
    {
        if (_executing)
        {
            return;
        }
        _executing = true;
        try
        {
            await OnClose.InvokeAsync();
        }
        finally
        {
            _executing = false;
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
        await _cts.CancelAsync();
        if (_watchTask is not null)
        {
            await _watchTask;
        }
        _cts.Dispose();
    }
}
