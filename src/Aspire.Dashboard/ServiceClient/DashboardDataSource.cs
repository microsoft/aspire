// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.ServiceClient;

internal interface IDashboardRunSelection
{
    DashboardRunDescriptor SelectedRun { get; }
    void SelectRun(string? runId);
}

/// <summary>
/// Provides repositories for the dashboard run selected in the current scope.
/// </summary>
public sealed class DashboardDataSource : IDashboardRunSelection, IDisposable
{
    private readonly IDashboardRunStore _runStore;
    private readonly ITelemetryRepository _currentTelemetryRepository;
    private readonly IResourceRepository _currentResourceRepository;
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly ILogger<DashboardDataSource> _logger;

    private ITelemetryRepository? _historicalTelemetryRepository;
    private IResourceRepository? _historicalResourceRepository;
    private DashboardSqliteDatabase? _historicalDatabase;
    private IDisposable? _historicalRunLease;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardDataSource"/> class.
    /// </summary>
    /// <param name="runStore">The store that provides available dashboard runs.</param>
    /// <param name="currentTelemetryRepository">The telemetry repository for the current dashboard run.</param>
    /// <param name="currentResourceRepository">The resource repository for the current dashboard run.</param>
    /// <param name="repositoryFactory">The factory used to create repositories for historical dashboard runs.</param>
    /// <param name="logger">The logger used to record dashboard run selection.</param>
    public DashboardDataSource(
        IDashboardRunStore runStore,
        ITelemetryRepository currentTelemetryRepository,
        IResourceRepository currentResourceRepository,
        IRepositoryFactory repositoryFactory,
        ILogger<DashboardDataSource> logger)
    {
        _runStore = runStore;
        _currentTelemetryRepository = currentTelemetryRepository;
        _currentResourceRepository = currentResourceRepository;
        _repositoryFactory = repositoryFactory;
        _logger = logger;

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

    DashboardRunDescriptor IDashboardRunSelection.SelectedRun => SelectedRun;

    void IDashboardRunSelection.SelectRun(string? runId) => SelectRun(runId);

    internal void SelectRun(string? runId)
    {
        var runs = _runStore.GetRuns();
        var currentRun = runs.Single(run => run.IsCurrent);
        var selectedRun = runs.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));
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
        DisposeHistoricalRepositories();
        _historicalTelemetryRepository = null;
        _historicalResourceRepository = null;

        if (!selectedRun.IsCurrent)
        {
            var historicalRunLease = _runStore.TryAcquireRunLease(selectedRun);
            if (historicalRunLease is null)
            {
                _logger.LogWarning("Failed to switch to dashboard run '{RunId}' because it is no longer available.", selectedRun.RunId);
                SelectCurrentRun(currentRun);
                LogRunSwitch(previousRun, currentRun);
                return;
            }

            DashboardSqliteDatabase? historicalDatabase = null;
            try
            {
                historicalDatabase = new DashboardSqliteDatabase(selectedRun.DatabasePath, readOnly: true);
                if (!historicalDatabase.ValidateSchemaVersion(selectedRun.SchemaVersion))
                {
                    throw new InvalidOperationException(
                        $"Dashboard database for run '{selectedRun.RunId}' does not match run metadata schema version '{selectedRun.SchemaVersion}'.");
                }
                _historicalTelemetryRepository = _repositoryFactory.CreateTelemetryRepository(historicalDatabase);
                _historicalResourceRepository = _repositoryFactory.CreateResourceRepository(historicalDatabase);
                _historicalDatabase = historicalDatabase;
                _historicalRunLease = historicalRunLease;
            }
            catch (Exception exception)
            {
                historicalDatabase?.Dispose();
                historicalRunLease.Dispose();
                _logger.LogWarning(exception, "Failed to switch to dashboard run '{RunId}'.", selectedRun.RunId);
                throw;
            }
            TelemetryRepository = _historicalTelemetryRepository;
            ResourceRepository = _historicalResourceRepository;
            IsReadOnly = true;
        }
        else
        {
            SelectCurrentRun(selectedRun);
        }

        SelectedRun = selectedRun;
        LogRunSwitch(previousRun, selectedRun);
    }

    public void Dispose()
    {
        DisposeHistoricalRepositories();
    }

    private void DisposeHistoricalRepositories()
    {
        _historicalTelemetryRepository?.Dispose();
        (_historicalResourceRepository as IDisposable)?.Dispose();
        _historicalDatabase?.Dispose();
        _historicalDatabase = null;
        _historicalRunLease?.Dispose();
        _historicalRunLease = null;
    }

    private void SelectCurrentRun(DashboardRunDescriptor currentRun)
    {
        TelemetryRepository = _currentTelemetryRepository;
        ResourceRepository = _currentResourceRepository;
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
