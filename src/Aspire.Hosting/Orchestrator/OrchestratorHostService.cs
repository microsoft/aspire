// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Orchestrator;

internal sealed class OrchestratorHostService : IHostedLifecycleService, IAsyncDisposable
{
    private readonly ApplicationOrchestrator _appOrchestrator;
    private readonly DcpHost _dcpHost;
    private readonly ILogger _logger;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private IAsyncDisposable? _dcpRunDisposable;

    public OrchestratorHostService(
        ILoggerFactory loggerFactory,
        DistributedApplicationExecutionContext executionContext,
        ApplicationOrchestrator appOrchestrator,
        DcpHost dcpHost)
    {
        _logger = loggerFactory.CreateLogger<OrchestratorHostService>();
        _executionContext = executionContext;
        _appOrchestrator = appOrchestrator;
        _dcpHost = dcpHost;
    }

    private bool IsSupported => !_executionContext.IsPublishMode;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return;
        }

        await _dcpHost.StartAsync(cancellationToken).ConfigureAwait(false);

        await _appOrchestrator.RunApplicationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _appOrchestrator.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dcpRunDisposable is { } disposable)
        {
            _dcpRunDisposable = null;

            try
            {
                await disposable.DisposeAsync().AsTask().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested.
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "One or more monitoring tasks terminated with an error.");
            }
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
