// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Controls the dashboard run selected in the current scope.
/// </summary>
public interface IDashboardRunSelection
{
    /// <summary>
    /// Gets the selected dashboard run.
    /// </summary>
    DashboardRunDescriptor SelectedRun { get; }

    /// <summary>
    /// Selects the dashboard run with the specified identifier.
    /// </summary>
    /// <param name="runId">The run identifier, or <see langword="null"/> to select the current run.</param>
    void SelectRun(string? runId);
}

/// <summary>
/// Provides repositories for the dashboard run selected in the current scope.
/// </summary>
public sealed class DashboardDataSource : IDashboardRunSelection, IDisposable
{
    private readonly IDashboardRunStore _runStore;
    private readonly ILogger<DashboardDataSource> _logger;
    private readonly DashboardDataSourcePool _dataSourcePool;

    private DashboardDataSourcePool.Lease? _historicalDataSourceLease;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardDataSource"/> class.
    /// </summary>
    /// <param name="runStore">The store that provides available dashboard runs.</param>
    /// <param name="logger">The logger used to record dashboard run selection.</param>
    /// <param name="dataSourcePool">The caller-owned pool that provides dashboard run data sources.</param>
    public DashboardDataSource(
        IDashboardRunStore runStore,
        ILogger<DashboardDataSource> logger,
        DashboardDataSourcePool dataSourcePool)
    {
        _runStore = runStore;
        _logger = logger;
        _dataSourcePool = dataSourcePool;

        SelectRun(runId: null);
    }

    internal DashboardRunDescriptor SelectedRun { get; private set; } = null!;

    /// <summary>
    /// Gets the telemetry repository for the selected dashboard run.
    /// </summary>
    public ITelemetryRepository TelemetryRepository { get; private set; } = null!;

    /// <summary>
    /// Gets the resource repository for the selected dashboard run.
    /// </summary>
    public IResourceRepository ResourceRepository { get; private set; } = null!;

    internal bool IsReadOnly { get; private set; }

    internal void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Historical dashboard data is read-only.");
        }
    }

    DashboardRunDescriptor IDashboardRunSelection.SelectedRun => SelectedRun;

    void IDashboardRunSelection.SelectRun(string? runId) => SelectRun(runId);

    internal void SelectRun(string? runId)
    {
        var currentRun = _runStore.GetCurrentRun();
        var selectedRun = runId is not null ? _runStore.GetRunById(runId) : null;
        if (selectedRun is null)
        {
            if (!string.IsNullOrEmpty(runId))
            {
                _logger.LogWarning("Failed to switch to dashboard run '{RunId}' because it is no longer available.", runId);
            }

            selectedRun = currentRun;
        }

        if (SelectedRun?.RunId == selectedRun.RunId)
        {
            return;
        }

        var previousRun = SelectedRun;

        if (!selectedRun.IsCurrent)
        {
            DashboardDataSourcePool.Lease? historicalDataSourceLease;
            try
            {
                historicalDataSourceLease = _dataSourcePool.TryAcquire(selectedRun);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to switch to dashboard run '{RunId}'.", selectedRun.RunId);
                throw;
            }

            if (historicalDataSourceLease is null)
            {
                _logger.LogWarning("Failed to switch to dashboard run '{RunId}' because it is no longer available.", selectedRun.RunId);
                return;
            }

            var previousHistoricalDataSourceLease = _historicalDataSourceLease;
            _historicalDataSourceLease = historicalDataSourceLease;
            TelemetryRepository = historicalDataSourceLease.TelemetryRepository;
            ResourceRepository = historicalDataSourceLease.ResourceRepository;
            IsReadOnly = true;
            SelectedRun = selectedRun;
            previousHistoricalDataSourceLease?.Dispose();
        }
        else
        {
            var previousHistoricalDataSourceLease = _historicalDataSourceLease;
            _historicalDataSourceLease = null;
            SelectCurrentRun(selectedRun);
            previousHistoricalDataSourceLease?.Dispose();
        }

        LogRunSwitch(previousRun, selectedRun);
    }

    public void Dispose()
    {
        DisposeHistoricalDataSource();
    }

    private void DisposeHistoricalDataSource()
    {
        _historicalDataSourceLease?.Dispose();
        _historicalDataSourceLease = null;
    }

    private void SelectCurrentRun(DashboardRunDescriptor currentRun)
    {
        var currentDataSource = _dataSourcePool.Current;
        TelemetryRepository = currentDataSource.TelemetryRepository;
        ResourceRepository = currentDataSource.ResourceRepository;
        IsReadOnly = false;
        SelectedRun = currentRun;
    }

    private void LogRunSwitch(DashboardRunDescriptor? previousRun, DashboardRunDescriptor selectedRun)
    {
        if (previousRun is not null && !string.Equals(previousRun.RunId, selectedRun.RunId, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Switched dashboard run from '{PreviousRunId}' to '{RunId}'.",
                previousRun.RunId,
                selectedRun.RunId);
        }
    }
}
