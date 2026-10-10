// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.ServiceClient;

internal sealed class DashboardDataSourceInitializer(
    DashboardDataSourcePool dataSourcePool,
    IHostApplicationLifetime applicationLifetime,
    ILogger<DashboardDataSourceInitializer> logger) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _startedRegistration;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await dataSourcePool.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Publishing from ApplicationStarted ensures a failed Kestrel bind doesn't leave an empty attempted run
        // in history. It is safe to publish this late because the current run is created from in-memory metadata;
        // run.json is only needed by a later Dashboard process to discover this run as historical. Pruning is slower
        // file system housekeeping, so keep that work off the startup callback.
        _startedRegistration = applicationLifetime.ApplicationStarted.Register(static state =>
        {
            var (pool, log) = ((DashboardDataSourcePool, ILogger))state!;
            pool.PublishRun();
            _ = Task.Run(() =>
            {
                try
                {
                    pool.PruneExpiredRuns();
                }
                catch (Exception ex)
                {
                    // Nothing awaits this, so an unhandled exception would crash the process.
                    log.LogWarning(ex, "Failed to prune expired dashboard runs.");
                }
            });
        }, (dataSourcePool, (ILogger)logger));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _startedRegistration.Dispose();
}