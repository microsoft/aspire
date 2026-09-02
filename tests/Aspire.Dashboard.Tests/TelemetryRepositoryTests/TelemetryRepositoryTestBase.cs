// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class TelemetryRepositoryTestBase
{
    protected async Task<RepositoryTestContext> CreateRepositoryAsync(
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
        TimeProvider? timeProvider = null,
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

        var temporaryDirectory = Directory.CreateTempSubdirectory("aspire-tests-dashboard-telemetry-").FullName;
        SqliteRepositoryTestContext<SqliteTelemetryRepository> context;
        try
        {
            context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(
                Path.Combine(temporaryDirectory, "dashboard.db"),
                pooling: true,
                loggerFactory: loggerFactory,
                dashboardOptions: options,
                pauseManager: pauseManager,
                timeProvider: timeProvider,
                outgoingPeerResolvers: outgoingPeerResolvers);
        }
        catch
        {
            Directory.Delete(temporaryDirectory, recursive: true);
            throw;
        }

        var repository = context.Repository;
        if (subscriptionMinExecuteInterval is not null)
        {
            repository.SubscriptionMinExecuteInterval = subscriptionMinExecuteInterval.Value;
        }
        return new RepositoryTestContext(
            repository,
            new TemporaryDirectoryRepositoryContext(context, temporaryDirectory));
    }

    protected sealed class RepositoryTestContext(
        ITelemetryRepository repository,
        IDisposable owner) : IDisposable
    {
        public ITelemetryRepository Repository { get; } = repository;

        public void Dispose() => owner.Dispose();
    }

    private sealed class TemporaryDirectoryRepositoryContext(IDisposable repositoryContext, string temporaryDirectory) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                repositoryContext.Dispose();
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }
}

internal static class TelemetryRepositoryTestExtensions
{
    public static ITelemetryRepositoryWriter AsWriter(this ITelemetryRepository repository) => (ITelemetryRepositoryWriter)repository;
}