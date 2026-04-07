// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.ManageData;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class ManageDataDialog : IDialogContentComponent, IAsyncDisposable
{
    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDialogService DialogService { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ConsoleLogsManager ConsoleLogsManager { get; init; }

    [Inject]
    public required TelemetryExportService TelemetryExportService { get; init; }

    [Inject]
    public required TelemetryImportService TelemetryImportService { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required ITelemetryErrorRecorder ErrorRecorder { get; init; }

    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly Dictionary<string, ResourceDataRow> _resourceDataRows = new(StringComparers.ResourceName);
    private readonly HashSet<string> _expandedResourceNames = new(StringComparers.ResourceName);
    private readonly HashSet<(string ResourceName, AspireDataType DataType)> _selectedRows = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Icon _iconUnselectedMultiple = new Icons.Regular.Size20.CheckboxUnchecked().WithColor(Color.FillInverse);
    private readonly Icon _iconSelectedMultiple = new Icons.Filled.Size20.CheckboxChecked();
    private readonly Icon _iconIndeterminate = new Icons.Filled.Size20.CheckboxIndeterminate();
    private Task? _resourceSubscriptionTask;
    private FluentDataGrid<ManageDataGridItem>? _dataGrid;
    private bool _isExporting;
    private bool _isRemoving;
    private string? _errorMessage;
    private bool _isImporting;
    private Subscription? _resourcesSubscription;

    protected override async Task OnInitializedAsync()
    {
        // Subscribe to telemetry changes
        _resourcesSubscription = TelemetryRepository.OnNewResources(OnTelemetryChangedAsync);

        if (DashboardClient.IsEnabled)
        {
            await ConsoleLogsManager.EnsureInitializedAsync();
            await SubscribeResourcesAsync();
        }

        // Initialize telemetry-only resources
        UpdateData();
    }

    private async Task OnTelemetryChangedAsync()
    {
        await InvokeAsync(async () =>
        {
            UpdateData();

            if (_dataGrid is not null)
            {
                await _dataGrid.SafeRefreshDataAsync();
            }

            StateHasChanged();
        });
    }

    private void UpdateData()
    {
        // Capture which resources have all data types selected before rebuilding
        var resourcesWithAllSelected = _resourceDataRows.Values
            .Where(AreAllDataRowsSelected)
            .Select(r => r.Name)
            .ToHashSet(StringComparers.ResourceName);
        var existingResourceNames = new HashSet<string>(_resourceDataRows.Keys, StringComparers.ResourceName);

        _resourceDataRows.Clear();

        // Recreate all resource rows from _resourceByName
        foreach (var (resourceName, resource) in _resourceByName)
        {
            _resourceDataRows[resourceName] = CreateResourceDataRow(resource);

            // If all data types were previously selected, select any new ones
            if (resourcesWithAllSelected.Contains(resourceName))
            {
                SelectAllDataTypesForResource(resourceName, _resourceDataRows[resourceName].TelemetryData);
            }
        }

        // Add telemetry-only resources
        foreach (var otlpResource in TelemetryRepository.GetResources())
        {
            var compositeName = otlpResource.ResourceKey.GetCompositeName();

            // Skip if this is already a resource from DashboardClient
            if (_resourceByName.ContainsKey(compositeName))
            {
                continue;
            }

            var isNewResource = !existingResourceNames.Contains(compositeName);
            _resourceDataRows[compositeName] = CreateTelemetryOnlyResourceDataRow(otlpResource);

            // Select all data types for new resources, or if all data types were previously selected
            if (isNewResource || resourcesWithAllSelected.Contains(compositeName))
            {
                SelectAllDataTypesForResource(compositeName, _resourceDataRows[compositeName].TelemetryData);
            }
        }

        // Remove selections for resources that no longer exist
        _selectedRows.RemoveWhere(r => !_resourceDataRows.ContainsKey(r.ResourceName));
    }

    private async Task SubscribeResourcesAsync()
    {
        var (snapshot, subscription) = await DashboardClient.SubscribeResourcesAsync(_cts.Token);

        // Apply snapshot.
        foreach (var resource in snapshot)
        {
            _resourceByName[resource.Name] = resource;
            _resourceDataRows[resource.Name] = CreateResourceDataRow(resource);

            // Select all data types for new resources by default
            SelectAllDataTypesForResource(resource.Name, _resourceDataRows[resource.Name].TelemetryData);
        }

        // Listen for updates and apply.
        _resourceSubscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        _resourceByName[resource.Name] = resource;
                        var isNewResource = !_resourceDataRows.ContainsKey(resource.Name);
                        _resourceDataRows[resource.Name] = CreateResourceDataRow(resource);

                        if (isNewResource)
                        {
                            // Select all data types for new resources by default
                            SelectAllDataTypesForResource(resource.Name, _resourceDataRows[resource.Name].TelemetryData);
                        }
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                        _resourceDataRows.Remove(resource.Name);
                        _expandedResourceNames.Remove(resource.Name);
                        RemoveAllSelectionsForResource(resource.Name);
                    }
                }

                await InvokeAsync(async () =>
                {
                    if (_dataGrid is not null)
                    {
                        await _dataGrid.SafeRefreshDataAsync();
                    }
                    StateHasChanged();
                });
            }
        });
    }

    private ResourceDataRow CreateResourceDataRow(ResourceViewModel resource)
    {
        var data = new List<TelemetryDataRow>();

        // Add resource details for resources with ResourceViewModel
        data.Add(CreateTelemetryDataRow(AspireDataType.ResourceDetails, resource.Name));

        // Add console logs for resources with ResourceViewModel
        data.Add(CreateTelemetryDataRow(AspireDataType.ConsoleLogs, resource.Name));

        var otlpResource = TelemetryRepository.GetResourceByCompositeName(resource.Name);
        if (otlpResource is not null)
        {
            PopulateDataRows(data, otlpResource, resource.Name);
        }

        return new ResourceDataRow
        {
            Resource = resource,
            OtlpResource = otlpResource,
            Name = resource.Name,
            TelemetryData = data
        };
    }

    private static ResourceDataRow CreateTelemetryOnlyResourceDataRow(OtlpResource otlpResource)
    {
        var data = new List<TelemetryDataRow>();
        var resourceName = otlpResource.ResourceKey.GetCompositeName();
        PopulateDataRows(data, otlpResource, resourceName);

        return new ResourceDataRow
        {
            Resource = null,
            OtlpResource = otlpResource,
            Name = otlpResource.ResourceKey.GetCompositeName(),
            TelemetryData = data
        };
    }

    private static void PopulateDataRows(List<TelemetryDataRow> data, OtlpResource otlpResource, string resourceName)
    {
        if (otlpResource.HasLogs)
        {
            data.Add(CreateTelemetryDataRow(AspireDataType.StructuredLogs, resourceName));
        }

        if (otlpResource.HasTraces)
        {
            data.Add(CreateTelemetryDataRow(AspireDataType.Traces, resourceName));
        }

        if (otlpResource.HasMetrics)
        {
            data.Add(CreateTelemetryDataRow(AspireDataType.Metrics, resourceName));
        }
    }

    private static TelemetryDataRow CreateTelemetryDataRow(AspireDataType dataType, string resourceName)
    {
        return dataType switch
        {
            AspireDataType.ResourceDetails => new TelemetryDataRow { DataType = AspireDataType.ResourceDetails, Icon = new Icons.Regular.Size16.ContentView(), Url = DashboardUrls.ResourcesUrl(resource: resourceName) },
            AspireDataType.ConsoleLogs => new TelemetryDataRow { DataType = AspireDataType.ConsoleLogs, Icon = new Icons.Regular.Size16.SlideText(), Url = DashboardUrls.ConsoleLogsUrl(resource: resourceName) },
            AspireDataType.StructuredLogs => new TelemetryDataRow { DataType = AspireDataType.StructuredLogs, Icon = new Icons.Regular.Size16.SlideTextSparkle(), Url = DashboardUrls.StructuredLogsUrl(resource: resourceName) },
            AspireDataType.Traces => new TelemetryDataRow { DataType = AspireDataType.Traces, Icon = new Icons.Regular.Size16.GanttChart(), Url = DashboardUrls.TracesUrl(resource: resourceName) },
            AspireDataType.Metrics => new TelemetryDataRow { DataType = AspireDataType.Metrics, Icon = new Icons.Regular.Size16.ChartMultiple(), Url = DashboardUrls.MetricsUrl(resource: resourceName) },
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };
    }

    private IQueryable<ManageDataGridItem> GetGridItems()
    {
        var items = new List<ManageDataGridItem>();

        // Sort by display name (works for both ResourceViewModel resources and telemetry-only resources)
        foreach (var resourceRow in _resourceDataRows.Values.OrderBy(r => r.Name, StringComparers.ResourceName))
        {
            // Add the resource row
            items.Add(new ManageDataGridItem
            {
                ResourceRow = resourceRow,
                Depth = 0
            });

            // If expanded, add nested data rows
            if (_expandedResourceNames.Contains(resourceRow.Name))
            {
                foreach (var dataRow in resourceRow.TelemetryData)
                {
                    items.Add(new ManageDataGridItem
                    {
                        NestedRow = dataRow,
                        ParentResourceName = resourceRow.Name,
                        Depth = 1
                    });
                }
            }
        }

        return items.AsQueryable();
    }

    private void OnRowClicked(FluentDataGridRow<ManageDataGridItem> row)
    {
        var item = row.Item;
        if (item is null)
        {
            return;
        }

        if (item.IsResourceRow && item.ResourceRow is not null)
        {
            OnSelectRowClicked(item.ResourceRow);
        }
        else if (item.IsNestedRow && item.NestedRow is not null && item.ParentResourceName is not null)
        {
            OnSelectDataRowClicked(item.ParentResourceName, item.NestedRow.DataType);
        }
    }

    private void OnToggleExpand(ResourceDataRow resourceRow)
    {
        if (_expandedResourceNames.Contains(resourceRow.Name))
        {
            _expandedResourceNames.Remove(resourceRow.Name);
        }
        else
        {
            _expandedResourceNames.Add(resourceRow.Name);
        }

        StateHasChanged();
    }

    private string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    private string GetOtlpResourceName(OtlpResource resource) => OtlpHelpers.GetResourceName(resource, TelemetryRepository.GetResources());

    private string GetDataTypeDisplayName(AspireDataType dataType) => dataType switch
    {
        AspireDataType.ResourceDetails => Loc[nameof(Resources.Dialogs.ManageDataResource)],
        AspireDataType.ConsoleLogs => Loc[nameof(Resources.Dialogs.ManageDataConsoleLogs)],
        AspireDataType.StructuredLogs => Loc[nameof(Resources.Dialogs.ManageDataStructuredLogs)],
        AspireDataType.Traces => Loc[nameof(Resources.Dialogs.ManageDataTraces)],
        AspireDataType.Metrics => Loc[nameof(Resources.Dialogs.ManageDataMetrics)],
        _ => dataType.ToString()
    };

    private static string GetItemKey(ManageDataGridItem item)
    {
        if (item.ResourceRow is not null)
        {
            return item.ResourceRow.Name;
        }
        if (item.NestedRow is not null && item.ParentResourceName is not null)
        {
            return $"{item.ParentResourceName}_{item.NestedRow.DataType}";
        }
        return string.Empty;
    }

    private void OnSelectAllClicked()
    {
        // If any are unselected (including data rows), select all. Otherwise deselect all.
        var shouldSelectAll = !AreAllSelected();

        if (shouldSelectAll)
        {
            foreach (var row in _resourceDataRows.Values)
            {
                SelectAllDataTypesForResource(row.Name, row.TelemetryData);
            }
        }
        else
        {
            _selectedRows.Clear();
        }
    }

    private void OnSelectRowClicked(ResourceDataRow row)
    {
        // If any child data rows are unselected, select all. Otherwise deselect all.
        var shouldSelect = !AreAllDataRowsSelected(row);

        if (shouldSelect)
        {
            SelectAllDataTypesForResource(row.Name, row.TelemetryData);
        }
        else
        {
            RemoveAllSelectionsForResource(row.Name);
        }
    }

    private void OnSelectDataRowClicked(string resourceName, AspireDataType dataType)
    {
        var key = (resourceName, dataType);
        if (!_selectedRows.Remove(key))
        {
            _selectedRows.Add(key);
        }
    }

    private bool IsResourceExpanded(string resourceName) => _expandedResourceNames.Contains(resourceName);

    private bool IsDataRowSelected(string resourceName, AspireDataType dataType) => _selectedRows.Contains((resourceName, dataType));

    /// <summary>
    /// Returns true if all resource rows and their data rows are selected.
    /// </summary>
    private bool AreAllSelected()
    {
        foreach (var row in _resourceDataRows.Values)
        {
            foreach (var dataRow in row.TelemetryData)
            {
                if (!_selectedRows.Contains((row.Name, dataRow.DataType)))
                {
                    return false;
                }
            }
        }
        return _resourceDataRows.Count > 0;
    }

    /// <summary>
    /// Returns true if no resource rows or data rows are selected.
    /// </summary>
    private bool AreNoneSelected()
    {
        return _selectedRows.Count == 0;
    }

    /// <summary>
    /// Returns true if all data rows for a resource are selected.
    /// </summary>
    private bool AreAllDataRowsSelected(ResourceDataRow row)
    {
        foreach (var dataRow in row.TelemetryData)
        {
            if (!_selectedRows.Contains((row.Name, dataRow.DataType)))
            {
                return false;
            }
        }
        return row.TelemetryData.Count > 0;
    }

    /// <summary>
    /// Returns true if no data rows for a resource are selected.
    /// </summary>
    private bool AreNoDataRowsSelected(ResourceDataRow row)
    {
        foreach (var dataRow in row.TelemetryData)
        {
            if (_selectedRows.Contains((row.Name, dataRow.DataType)))
            {
                return false;
            }
        }
        return true;
    }

    private Icon GetHeaderCheckboxIcon()
    {
        if (AreAllSelected())
        {
            return _iconSelectedMultiple;
        }
        if (AreNoneSelected())
        {
            return _iconUnselectedMultiple;
        }
        return _iconIndeterminate;
    }

    private Icon GetResourceCheckboxIcon(ResourceDataRow row)
    {
        if (AreAllDataRowsSelected(row))
        {
            return _iconSelectedMultiple;
        }
        if (AreNoDataRowsSelected(row))
        {
            return _iconUnselectedMultiple;
        }
        return _iconIndeterminate;
    }

    private void NavigateToDataPage(TelemetryDataRow dataRow)
    {
        NavigationManager.NavigateTo(dataRow.Url);
    }

    private async Task RemoveSelectedAsync()
    {
        if (_isRemoving)
        {
            return;
        }

        _isRemoving = true;
        _errorMessage = null;
        StateHasChanged();

        try
        {
            var selectedResources = GetSelectedResourcesAndDataTypes();

            // Clear telemetry signals via repository
            TelemetryRepository.ClearSelectedSignals(selectedResources);

            // Handle console logs filtering separately (not stored in TelemetryRepository)
            // Console logs are only available when the dashboard client is enabled
            if (DashboardClient.IsEnabled)
            {
                var consoleLogResourcesToFilter = selectedResources
                    .Where(kvp => kvp.Value.Contains(AspireDataType.ConsoleLogs))
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (consoleLogResourcesToFilter.Count > 0)
                {
                    var filterDate = TimeProvider.GetUtcNow().UtcDateTime;
                    var filters = ConsoleLogsManager.Filters;
                    foreach (var resourceName in consoleLogResourcesToFilter)
                    {
                        filters = filters.WithResourceCleared(resourceName, filterDate);
                    }
                    await ConsoleLogsManager.UpdateFiltersAsync(filters);
                }
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"{Loc[nameof(Resources.Dialogs.ManageDataRemoveErrorMessage)]}: {ex.Message}";
            ErrorRecorder.RecordError("Failed to remove data", ex, writeToLogging: true);
        }
        finally
        {
            _isRemoving = false;
            StateHasChanged();
        }

        await OnTelemetryChangedAsync();
    }

    private void OnInputFileProgressChange(FluentInputFileEventArgs args)
    {
        _isImporting = true;
        _errorMessage = null;
    }

    private async Task OnInputFileCompleted(IEnumerable<FluentInputFileEventArgs> args)
    {
        try
        {
            var files = args.ToList();

            foreach (var file in files)
            {
                if (file.LocalFile is not null)
                {
                    using var fileStream = file.LocalFile.OpenRead();
                    await TelemetryImportService.ImportAsync(file.Name, fileStream, CancellationToken.None);
                    await OnTelemetryChangedAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"{Loc[nameof(Resources.Dialogs.ManageDataImportErrorMessage)]}: {ex.Message}";
            ErrorRecorder.RecordError("Failed to import data", ex, writeToLogging: true);
        }
        finally
        {
            _isImporting = false;
            StateHasChanged();
        }
    }

    private async Task ExportSelectedAsync()
    {
        if (_isExporting)
        {
            return;
        }

        _isExporting = true;
        _errorMessage = null;
        StateHasChanged();

        try
        {
            var selectedResources = GetSelectedResourcesAndDataTypes();
            using var memoryStream = await TelemetryExportService.ExportSelectedAsync(selectedResources, _cts.Token);
            var fileName = $"aspire-telemetry-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

            await JS.DownloadFileAsync(fileName, memoryStream);
        }
        catch (Exception ex)
        {
            _errorMessage = $"{Loc[nameof(Resources.Dialogs.ManageDataExportErrorMessage)]}: {ex.Message}";
            ErrorRecorder.RecordError("Failed to export data", ex, writeToLogging: true);
        }
        finally
        {
            _isExporting = false;
            StateHasChanged();
        }
    }

    private Dictionary<string, HashSet<AspireDataType>> GetSelectedResourcesAndDataTypes()
    {
        var result = new Dictionary<string, HashSet<AspireDataType>>(StringComparers.ResourceName);

        foreach (var (resourceName, dataType) in _selectedRows)
        {
            if (!result.TryGetValue(resourceName, out var dataTypes))
            {
                dataTypes = [];
                result[resourceName] = dataTypes;
            }
            dataTypes.Add(dataType);
        }

        // If all available data types for a resource are selected, add the Resource flag
        // to indicate the resource itself should be removed
        foreach (var (resourceName, dataTypes) in result)
        {
            if (_resourceDataRows.TryGetValue(resourceName, out var resourceRow))
            {
                var allAvailableSelected = resourceRow.TelemetryData.All(d => dataTypes.Contains(d.DataType));
                if (allAvailableSelected)
                {
                    dataTypes.Add(AspireDataType.Resource);
                }
            }
        }

        return result;
    }

    private void SelectAllDataTypesForResource(string resourceName, List<TelemetryDataRow> dataRows)
    {
        foreach (var dataRow in dataRows)
        {
            _selectedRows.Add((resourceName, dataRow.DataType));
        }
    }

    private void RemoveAllSelectionsForResource(string resourceName)
    {
        _selectedRows.RemoveWhere(r => string.Equals(r.ResourceName, resourceName, StringComparisons.ResourceName));
    }

    public async ValueTask DisposeAsync()
    {
        _resourcesSubscription?.Dispose();

        await _cts.CancelAsync();

        if (_resourceSubscriptionTask is not null)
        {
            try
            {
                await _resourceSubscriptionTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
        }

        _cts.Dispose();
    }
}
