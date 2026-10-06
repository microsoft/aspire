// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

public sealed partial class Terminals : ComponentBase, IAsyncDisposable, IComponentWithTelemetry,
    IPageWithSessionAndUrlState<Terminals.TerminalsViewModel, Terminals.TerminalsPageState>
{
    private readonly CancellationTokenSource _cts = new();
    // URL restoration can read resource names off the renderer while the watch updates this map.
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private ImmutableList<SelectViewModel<ResourceTypeDetails>>? _resources;
    private readonly List<CommandViewModel> _highlightedCommands = [];
    private readonly List<MenuButtonItem> _resourceMenuItems = [];
    private readonly List<MenuButtonItem> _optionsMenuItems = [];
    private AspirePageContentLayout? _contentLayout;
    private Task? _watchTask;
    private bool _showHiddenResources;
    private bool _redirected;
    private bool _disposed;

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required IStringLocalizer<TerminalStrings> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ConsoleLogs> ConsoleLoc { get; init; }

    [Inject]
    public required ILogger<Terminals> Logger { get; init; }

    [Inject]
    public required ResourceMenuBuilder ResourceMenuBuilder { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    [Parameter]
    public string? ResourceName { get; set; }

    public string BasePath => DashboardUrls.TerminalsBasePath;
    public string SessionStorageKey => BrowserStorageKeys.TerminalsPageState;
    public TerminalsViewModel PageViewModel { get; set; } = new();
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.Terminals);

    protected override async Task OnInitializedAsync()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        if (!DashboardClient.IsEnabled || DashboardClient.IsReadOnly)
        {
            RedirectToResources();
            return;
        }

        var cancellationToken = _cts.Token;
        try
        {
            var hidden = await SessionStorage.GetAsync<bool>(BrowserStorageKeys.ResourcesShowHiddenResources).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _showHiddenResources = hidden.Success && hidden.Value;
            // Connection readiness precedes persistence of the initial resource snapshot.
            await DashboardClient.WhenResourcesReady.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var (snapshot, updates) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var resource in snapshot)
            {
                _resourceByName[resource.Name] = resource;
            }
            UpdateResources();
            _watchTask = WatchResourcesAsync(updates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_disposed || _redirected || _resources is null)
        {
            return;
        }

        if (!_resourceByName.Values.Any(ResourceSelectHelpers.HasUsableTerminal))
        {
            RedirectToResources();
            return;
        }

        if (await this.InitializeViewModelAsync())
        {
            return;
        }
        UpdateMenus();
    }

    private async Task WatchResourcesAsync(IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> updates, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var changes in updates.WithCancellation(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    if (cancellationToken.IsCancellationRequested || _redirected)
                    {
                        return;
                    }

                    var previousSelection = PageViewModel.SelectedResource?.Id?.InstanceId;
                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resourceByName.TryRemove(resource.Name, out _);
                        }
                        else
                        {
                            _resourceByName[resource.Name] = resource;
                        }
                    }
                    UpdateResources();
                    if (!_resourceByName.Values.Any(ResourceSelectHelpers.HasUsableTerminal))
                    {
                        RedirectToResources();
                        return;
                    }

                    PageViewModel.SelectedResource = FindResource(previousSelection);
                    UpdateMenus();
                    if (!string.Equals(previousSelection, PageViewModel.SelectedResource?.Id?.InstanceId, StringComparisons.ResourceName))
                    {
                        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
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
            Logger.LogError(ex, "Failed to watch terminal resources.");
            await DispatchExceptionAsync(ex);
        }
    }

    private void UpdateResources()
    {
        var terminals = _resourceByName.Values.Where(ResourceSelectHelpers.HasUsableTerminal)
            .ToDictionary(r => r.Name, StringComparers.ResourceName);
        _resources = ResourceSelectHelpers.CreateOptions(terminals, ConsoleLoc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsUnknownState)], _showHiddenResources)
            .Insert(0, new() { Id = null, Name = ControlsLoc[nameof(ControlsStrings.LabelNone)] });
    }

    private SelectViewModel<ResourceTypeDetails>? FindResource(string? name)
    {
        if (_resources is null)
        {
            return null;
        }

        return _resources.GetResource(Logger, name, canSelectGrouping: false, fallbackViewModel: _resources[0]);
    }

    public async Task UpdateViewModelFromQueryAsync(TerminalsViewModel viewModel)
    {
        viewModel.SelectedResource = FindResource(ResourceName);
        var url = GetUrlFromSerializableViewModel(ConvertViewModelToSerializable());
        await SessionStorage.SetAsync(SessionStorageKey, ConvertViewModelToSerializable());
        if (NavigationManager.ToBaseRelativePath(NavigationManager.Uri) != url.TrimStart('/'))
        {
            NavigationManager.NavigateTo(url, new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    public string GetUrlFromSerializableViewModel(TerminalsPageState serializable)
    {
        var resource = FindResource(serializable.SelectedResource);
        return DashboardUrls.TerminalsUrl(GetResourceName(resource));
    }

    public TerminalsPageState ConvertViewModelToSerializable()
        => new(GetResourceName(PageViewModel.SelectedResource));

    private string? GetResourceName(SelectViewModel<ResourceTypeDetails>? selectedResource)
        => selectedResource?.Id?.InstanceId is { } name && _resourceByName.TryGetValue(name, out var resource)
            ? ResourceViewModel.GetResourceName(resource, _resourceByName)
            : null;

    private ResourceViewModel? GetSelectedResource()
        => PageViewModel.SelectedResource?.Id?.InstanceId is { } name
            ? _resourceByName.GetValueOrDefault(name)
            : null;

    private async Task HandleSelectedResourceChangedAsync()
    {
        UpdateMenus();
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private void UpdateMenus()
    {
        _highlightedCommands.Clear();
        _resourceMenuItems.Clear();
        _optionsMenuItems.Clear();
        CommonMenuItems.AddToggleHiddenResourcesMenuItem(_optionsMenuItems, ControlsLoc, _showHiddenResources,
            _resourceByName.Values.Where(ResourceSelectHelpers.HasUsableTerminal), SessionStorage,
            EventCallback.Factory.Create<bool>(this, async showHidden =>
            {
                _showHiddenResources = showHidden;
                var previousSelection = PageViewModel.SelectedResource?.Id?.InstanceId;
                UpdateResources();
                PageViewModel.SelectedResource = FindResource(previousSelection);
                UpdateMenus();
                await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
            }));

        if (GetSelectedResource() is { } resource)
        {
            if (ViewportInformation.IsDesktop)
            {
                _highlightedCommands.AddRange(resource.Commands.Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden)
                    .Take(DashboardUIHelpers.MaxHighlightedCommands));
            }
            ResourceMenuBuilder.AddMenuItems(_resourceMenuItems, resource, _resourceByName,
                EventCallback.Factory.Create(this, () => NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(resource: resource.Name))),
                EventCallback.Factory.Create<CommandViewModel>(this, ExecuteResourceCommandAsync),
                (r, command) => DashboardCommandExecutor.IsExecuting(r.Name, command.Name),
                showViewDetails: true, showTerminalItem: false, showConsoleLogsItem: true, showUrls: true);
        }
    }

    private Task ExecuteResourceCommandAsync(CommandViewModel command)
    {
        if (GetSelectedResource() is not { } resource)
        {
            Logger.LogWarning("No terminal resource selected for command execution.");
            return Task.CompletedTask;
        }

        return DashboardCommandExecutor.ExecuteAsync(resource, command, r => ResourceViewModel.GetResourceName(r, _resourceByName));
    }

    private void RedirectToResources()
    {
        _redirected = true;
        NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
    }

    public void UpdateTelemetryProperties()
    {
    }

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
        TelemetryContext.Dispose();
    }

    public sealed class TerminalsViewModel
    {
        public SelectViewModel<ResourceTypeDetails>? SelectedResource { get; set; }
    }

    public sealed record TerminalsPageState(string? SelectedResource);
}
