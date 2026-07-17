// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class TelemetryRepositoryTestBase : IDisposable
{
    private readonly List<ITelemetryRepository> _repositories = [];
    private readonly List<string> _temporaryDirectories = [];

    protected abstract bool UseSqlite { get; }

    protected ITelemetryRepository CreateRepository(
        int? maxMetricsCount = null,
        int? maxAttributeCount = null,
        int? maxAttributeLength = null,
        int? maxSpanEventCount = null,
        int? maxTraceCount = null,
        int? maxLogCount = null,
        int? maxResourceCount = null,
        TimeSpan? subscriptionMinExecuteInterval = null,
        ILoggerFactory? loggerFactory = null,
        global::Aspire.Dashboard.Model.PauseManager? pauseManager = null,
        global::Aspire.Dashboard.Model.IOutgoingPeerResolver[]? outgoingPeerResolvers = null)
    {
        var telemetryLimits = new global::Aspire.Dashboard.Configuration.TelemetryLimitOptions();
        telemetryLimits.MaxMetricsCount = maxMetricsCount ?? telemetryLimits.MaxMetricsCount;
        telemetryLimits.MaxAttributeCount = maxAttributeCount ?? telemetryLimits.MaxAttributeCount;
        telemetryLimits.MaxAttributeLength = maxAttributeLength ?? telemetryLimits.MaxAttributeLength;
        telemetryLimits.MaxSpanEventCount = maxSpanEventCount ?? telemetryLimits.MaxSpanEventCount;
        telemetryLimits.MaxTraceCount = maxTraceCount ?? telemetryLimits.MaxTraceCount;
        telemetryLimits.MaxLogCount = maxLogCount ?? telemetryLimits.MaxLogCount;
        telemetryLimits.MaxResourceCount = maxResourceCount ?? telemetryLimits.MaxResourceCount;

        loggerFactory ??= NullLoggerFactory.Instance;
        pauseManager ??= new global::Aspire.Dashboard.Model.PauseManager();
        outgoingPeerResolvers ??= [];
        var options = Options.Create(new global::Aspire.Dashboard.Configuration.DashboardOptions { TelemetryLimits = telemetryLimits });

        ITelemetryRepository repository;
        if (UseSqlite)
        {
            var temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-telemetry-tests-").FullName;
            _temporaryDirectories.Add(temporaryDirectory);
            var sqliteRepository = new SqliteTelemetryRepository(
                Path.Combine(temporaryDirectory, "dashboard.db"),
                loggerFactory,
                options,
                pauseManager,
                outgoingPeerResolvers);
            if (subscriptionMinExecuteInterval is not null)
            {
                sqliteRepository.SubscriptionMinExecuteInterval = subscriptionMinExecuteInterval.Value;
            }
            repository = sqliteRepository;
        }
        else
        {
            var inMemoryRepository = new InMemoryTelemetryRepository(loggerFactory, options, pauseManager, outgoingPeerResolvers);
            if (subscriptionMinExecuteInterval is not null)
            {
                inMemoryRepository._subscriptionMinExecuteInterval = subscriptionMinExecuteInterval.Value;
            }
            repository = inMemoryRepository;
        }

        _repositories.Add(repository);
        return repository;
    }

    public void Dispose()
    {
        foreach (var repository in _repositories)
        {
            repository.Dispose();
        }

        foreach (var temporaryDirectory in _temporaryDirectories)
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

internal static class TelemetryRepositoryTestExtensions
{
    public static ITelemetryRepositoryWriter AsWriter(this ITelemetryRepository repository) => (ITelemetryRepositoryWriter)repository;
}