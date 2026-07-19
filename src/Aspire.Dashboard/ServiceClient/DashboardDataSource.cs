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

    private ITelemetryRepository? _historicalTelemetryRepository;
    private IResourceRepository? _historicalResourceRepository;
    private DashboardSqliteDatabase? _historicalDatabase;
    private IDisposable? _historicalRunLease;

    internal DashboardDataSource(
        IDashboardRunStore runStore,
        ITelemetryRepository currentTelemetryRepository,
        IResourceRepository currentResourceRepository,
        IRepositoryFactory repositoryFactory)
    {
        _runStore = runStore;
        _currentTelemetryRepository = currentTelemetryRepository;
        _currentResourceRepository = currentResourceRepository;
        _repositoryFactory = repositoryFactory;

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
        var selectedRun = runs.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal))
            ?? runs.Single(run => run.IsCurrent);
        if (SelectedRun?.RunId == selectedRun.RunId)
        {
            return;
        }

        DisposeHistoricalRepositories();
        _historicalTelemetryRepository = null;
        _historicalResourceRepository = null;

        if (!selectedRun.IsCurrent)
        {
            var historicalRunLease = _runStore.TryAcquireRunLease(selectedRun)
                ?? throw new InvalidOperationException($"Dashboard run '{selectedRun.RunId}' is no longer available.");
            var historicalDatabase = new DashboardSqliteDatabase(selectedRun.DatabasePath, readOnly: true);
            try
            {
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
            catch
            {
                historicalDatabase.Dispose();
                historicalRunLease.Dispose();
                throw;
            }
            TelemetryRepository = _historicalTelemetryRepository;
            ResourceRepository = _historicalResourceRepository;
            IsReadOnly = true;
        }
        else
        {
            TelemetryRepository = _currentTelemetryRepository;
            ResourceRepository = _currentResourceRepository;
            IsReadOnly = false;
        }

        SelectedRun = selectedRun;
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
}
