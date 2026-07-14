// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

internal interface IDashboardRunSelection
{
    void SelectRun(string? runId);
}

internal sealed class DashboardDataSource : IDashboardRunSelection, IDisposable
{
    private readonly DashboardRunStore _runStore;
    private readonly SqliteTelemetryRepository _currentTelemetryRepository;
    private readonly SqliteResourceRepository _currentResourceRepository;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<DashboardOptions> _dashboardOptions;
    private readonly PauseManager _pauseManager;
    private readonly IEnumerable<IOutgoingPeerResolver> _outgoingPeerResolvers;
    private readonly IKnownPropertyLookup _knownPropertyLookup;

    private SqliteTelemetryRepository? _historicalTelemetryRepository;
    private SqliteResourceRepository? _historicalResourceRepository;

    public DashboardDataSource(
        DashboardRunStore runStore,
        SqliteTelemetryRepository currentTelemetryRepository,
        SqliteResourceRepository currentResourceRepository,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> dashboardOptions,
        PauseManager pauseManager,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers,
        IKnownPropertyLookup knownPropertyLookup)
    {
        _runStore = runStore;
        _currentTelemetryRepository = currentTelemetryRepository;
        _currentResourceRepository = currentResourceRepository;
        _loggerFactory = loggerFactory;
        _dashboardOptions = dashboardOptions;
        _pauseManager = pauseManager;
        _outgoingPeerResolvers = outgoingPeerResolvers;
        _knownPropertyLookup = knownPropertyLookup;

        SelectRun(runId: null);
    }

    public DashboardRunDescriptor SelectedRun { get; private set; } = null!;
    public ITelemetryRepository TelemetryRepository { get; private set; } = null!;
    public IResourceRepository ResourceRepository { get; private set; } = null!;
    public bool IsReadOnly { get; private set; }

    public void SelectRun(string? runId)
    {
        var runs = _runStore.GetRuns();
        var selectedRun = runs.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal))
            ?? runs.Single(run => run.IsCurrent);
        if (SelectedRun?.RunId == selectedRun.RunId)
        {
            return;
        }

        _historicalTelemetryRepository?.Dispose();
        _historicalResourceRepository?.Dispose();
        _historicalTelemetryRepository = null;
        _historicalResourceRepository = null;

        if (!selectedRun.IsCurrent)
        {
            var historicalDatabase = new DashboardSqliteDatabase(selectedRun.DatabasePath, readOnly: true);
            _historicalTelemetryRepository = new SqliteTelemetryRepository(
                historicalDatabase,
                _loggerFactory,
                _dashboardOptions,
                _pauseManager,
                _outgoingPeerResolvers);
            _historicalResourceRepository = new SqliteResourceRepository(
                historicalDatabase,
                _knownPropertyLookup,
                _loggerFactory);
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
        _historicalTelemetryRepository?.Dispose();
        _historicalResourceRepository?.Dispose();
    }
}