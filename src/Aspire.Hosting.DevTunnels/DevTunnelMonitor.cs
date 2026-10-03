// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels.Resources;
using Aspire.Hosting.Eventing;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

/// <summary>
/// Serializes observations of one local tunnel host and its remote configuration.
/// </summary>
internal sealed class DevTunnelMonitor : IDisposable, IAsyncDisposable
{
    internal const string DiagnosticPrefix = "[Aspire dev tunnels] ";

    private readonly DevTunnelResource _resource;
    private readonly IServiceProvider _services;
    private readonly IDevTunnelClient _client;
    private readonly ResourceLoggerService _logs;
    private readonly ResourceNotificationService _notifications;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping;
    private readonly Channel<Operation> _operations = Channel.CreateUnbounded<Operation>(new()
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly Task _processing;
    private readonly Dictionary<DevTunnelPortResource, Task> _endpointEvents = [];
    private readonly HashSet<DevTunnelPortResource> _allocatedEndpoints = [];
    private readonly Dictionary<DevTunnelPortResource, Exception> _endpointErrors = [];
    private Run? _run;
    private int _disposed;

    internal TimeSpan StartupLogTimeout { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ReconciliationRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    public DevTunnelMonitor(DevTunnelResource resource, IServiceProvider services)
    {
        _resource = resource;
        _services = services;
        _client = services.GetRequiredService<IDevTunnelClient>();
        _logs = services.GetRequiredService<ResourceLoggerService>();
        _notifications = services.GetRequiredService<ResourceNotificationService>();
        _logger = _logs.GetLogger(resource);
        _timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        _processing = ProcessAsync();
    }

    public async Task StartAsync(string resolvedTunnelId, CancellationToken cancellationToken)
    {
        var ports = new Dictionary<DevTunnelPortResource, int>();
        foreach (var port in _resource.Ports)
        {
            ports.Add(port, await port.GetTunnelPortAsync(cancellationToken).ConfigureAwait(false));
        }

        await InvokeAsync(async () =>
        {
            await StopRunAsync().ConfigureAwait(false);
            var run = new Run(resolvedTunnelId, ports, _resource.TunnelId, _timeProvider.GetUtcNow(), _stopping.Token);
            _run = run;
            _resource.LastKnownStatus = null;
            _resource.LastKnownAccessStatus = null;
            foreach (var port in ports.Keys)
            {
                port.LastKnownStatus = null;
                port.LastKnownAccessStatus = null;
            }

            await PublishAsync(run).ConfigureAwait(false);
            // Subscribe only after BeforeResourceStarted has resolved the DCP instance names.
            // Keeping this subscription for the run also makes observation independent of the dashboard.
            run.LogTask = WatchLogsAsync(run);
            run.StateTask = WatchStateAsync(run);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => StopAndPublishAsync(cancellationToken), cancellationToken);

    private async Task<bool> StopAndPublishAsync(CancellationToken cancellationToken)
    {
        await StopRunAsync().ConfigureAwait(false);
        _resource.LastKnownStatus = null;
        _resource.LastKnownAccessStatus = null;
        foreach (var port in _resource.Ports)
        {
            await DevTunnelsResourceBuilderExtensions.StopPortAsync(port, _services, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var healthTask = await InvokeAsync(() =>
        {
            if (_run is not { } current)
            {
                return Task.FromResult<Task?>(null);
            }
            if (!current.Ready.Task.IsCompletedSuccessfully)
            {
                current.InitialHealthTask ??= MonitorInitialHealthAsync(current);
                return Task.FromResult<Task?>(current.Ready.Task);
            }
            return Task.FromResult<Task?>(BeginReconciliation(current));
        }, cancellationToken).ConfigureAwait(false);

        if (healthTask is null)
        {
            return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, MessageStrings.DevTunnelHostNotReady, _resource.TunnelId));
        }

        await healthTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        // This registration gates the one-time ResourceReadyEvent. The monitor owns a separate
        // live health report so a health evaluation already in flight cannot undo a disconnect.
        return HealthCheckResult.Healthy();
    }

    internal Task ProcessLogAsync(string content, CancellationToken cancellationToken) => InvokeAsync(async () =>
    {
        if (_run is { } run && IsCurrent(run))
        {
            await ApplyLogsAsync(run, [new(1, content, false)]).ConfigureAwait(false);
        }
        return true;
    }, cancellationToken);

    private async Task MonitorInitialHealthAsync(Run run)
    {
        try
        {
            // Normally the log stream completes readiness before this delay. Only the fallback
            // polls, using the same service observation as subsequent health evaluations.
            try
            {
                await run.Ready.Task.WaitAsync(StartupLogTimeout, _timeProvider, run.Cancellation.Token).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Missing or changed output must not prevent the service-based fallback.
            }

            await InvokeAsync(() =>
            {
                if (!run.HasReadyMessage || !run.ExpectedPorts.Values.All(run.Ports.ContainsKey))
                {
                    WarnUnrecognizedOutput(run);
                }
                return Task.FromResult(true);
            }, run.Cancellation.Token).ConfigureAwait(false);

            while (!run.Ready.Task.IsCompleted)
            {
                await ReconcileAsync(run, run.Cancellation.Token).ConfigureAwait(false);
                if (!run.Ready.Task.IsCompleted)
                {
                    await Task.Delay(ReconciliationRetryInterval, _timeProvider, run.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, DiagnosticPrefix + "Failed to monitor initial tunnel readiness.");
            run.Ready.TrySetException(ex);
        }
    }

    private async Task ReconcileAsync(Run run, CancellationToken cancellationToken)
    {
        var task = await InvokeAsync(() => Task.FromResult(BeginReconciliation(run)), cancellationToken).ConfigureAwait(false);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BeginReconciliation(Run run)
    {
        if (!IsCurrent(run))
        {
            return Task.CompletedTask;
        }
        // Coalesce overlapping health evaluations without holding a lock over CLI calls.
        if (run.ReconciliationTask.IsCompleted)
        {
            run.ReconciliationTask = QueryServiceAsync(run, run.Revision);
        }
        return run.ReconciliationTask;
    }

    private async Task QueryServiceAsync(Run run, long revision)
    {
        var cancellationToken = run.Cancellation.Token;
        var logger = _services.GetRequiredService<ILogger<DevTunnelHealthCheck>>();
        try
        {
            var status = await _client.GetTunnelAsync(run.TunnelId, logger, cancellationToken).ConfigureAwait(false);
            var wasReady = await InvokeAsync(async () =>
            {
                var alreadyReady = run.Ready.Task.IsCompletedSuccessfully;
                if (IsCurrent(run) && revision == run.Revision)
                {
                    run.TunnelId = status.TunnelId;
                    _resource.LastKnownStatus = status;
                    // Aggregate service connections may belong to another host. They cannot
                    // override an explicit disconnect observed from this local process.
                    run.Connected = !run.LocallyDisconnected && status.HostConnections > 0;
                    run.Ports.Clear();
                    foreach (var port in status.Ports)
                    {
                        // The structured service response identifies the port independently of its
                        // hostname. Do not impose the console parser's URL naming convention here.
                        if (port.PortUri is { IsAbsoluteUri: true, Scheme: "https" } uri)
                        {
                            run.Ports[port.PortNumber] = uri;
                        }
                    }
                    run.Error = null;
                    await PublishAsync(run).ConfigureAwait(false);
                }
                return alreadyReady;
            }, cancellationToken).ConfigureAwait(false);

            var accessTask = await InvokeAsync(() =>
            {
                if (IsCurrent(run) && wasReady && run.AccessTask.IsCompleted)
                {
                    run.AccessTask = QueryAccessAsync(run);
                }
                return Task.FromResult(run.AccessTask);
            }, cancellationToken).ConfigureAwait(false);
            await accessTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, DiagnosticPrefix + "Failed to reconcile dev tunnel status.");
            try
            {
                await InvokeAsync(async () =>
                {
                    if (IsCurrent(run) && revision == run.Revision)
                    {
                        run.Error = ex.Message;
                        if (ex is DevTunnelNotFoundException)
                        {
                            _resource.LastKnownStatus = null;
                            run.Connected = false;
                            run.Ports.Clear();
                        }
                        else if (!run.HasReadyMessage)
                        {
                            run.Connected = false;
                        }
                        await PublishAsync(run).ConfigureAwait(false);
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);

                await CheckLoginAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task QueryAccessAsync(Run run)
    {
        var cancellationToken = run.Cancellation.Token;
        var logger = _services.GetRequiredService<ILogger<DevTunnelHealthCheck>>();
        try
        {
            var tunnelAccess = await _client.GetAccessAsync(run.TunnelId, portNumber: null, logger, cancellationToken).ConfigureAwait(false);
            var portAccess = new Dictionary<DevTunnelPortResource, DevTunnelAccessStatus>();
            foreach (var (port, number) in run.ExpectedPorts)
            {
                portAccess.Add(port, await _client.GetAccessAsync(run.TunnelId, number, logger, cancellationToken).ConfigureAwait(false));
            }
            await InvokeAsync(async () =>
            {
                if (IsCurrent(run))
                {
                    _resource.LastKnownAccessStatus = tunnelAccess;
                    foreach (var (port, access) in portAccess)
                    {
                        port.LastKnownAccessStatus = access;
                        await DevTunnelsResourceBuilderExtensions.UpdatePortAccessAsync(port, _services).ConfigureAwait(false);
                    }
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, DiagnosticPrefix + "Failed to refresh tunnel access metadata.");
            try
            {
                await CheckLoginAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private Task CheckLoginAsync(Run run) => InvokeAsync(() =>
    {
        if (IsCurrent(run) && run.LoginTask.IsCompleted)
        {
            // A notification waits for user interaction. Do not let it hold up reconciliation.
            run.LoginTask = NotifyIfLoggedOutAsync(run.Cancellation.Token);
        }
        return Task.FromResult(true);
    }, run.Cancellation.Token);

    private async Task NotifyIfLoggedOutAsync(CancellationToken cancellationToken)
    {
        var logger = _services.GetRequiredService<ILogger<DevTunnelHealthCheck>>();
        try
        {
            var login = await _client.GetUserLoginStatusAsync(logger, cancellationToken).ConfigureAwait(false);
            if (!login.IsLoggedIn)
            {
                await _services.GetRequiredService<LoggedOutNotificationManager>()
                    .NotifyUserLoggedOutAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check dev tunnels login status or notify the user.");
        }
    }

    private async Task WatchLogsAsync(Run run)
    {
        try
        {
            await foreach (var batch in _logs.WatchAsync(_resource).WithCancellation(run.Cancellation.Token).ConfigureAwait(false))
            {
                await InvokeAsync(async () =>
                {
                    if (IsCurrent(run))
                    {
                        await ApplyLogsAsync(run, batch).ConfigureAwait(false);
                    }
                    return true;
                }, run.Cancellation.Token).ConfigureAwait(false);
            }
            if (!run.Cancellation.IsCancellationRequested)
            {
                _logger.LogWarning(DiagnosticPrefix + "The tunnel log stream ended. Health checks will continue reconciling tunnel status.");
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, DiagnosticPrefix + "Unable to watch devtunnel output. Health checks will continue reconciling tunnel status.");
        }
    }

    private async Task WatchStateAsync(Run run)
    {
        try
        {
            await foreach (var update in _notifications.WatchAsync(run.Cancellation.Token).ConfigureAwait(false))
            {
                if (ReferenceEquals(update.Resource, _resource)
                    && KnownResourceStates.TerminalStates.Contains(update.Snapshot.State?.Text))
                {
                    // Failed-to-start resources don't necessarily raise ResourceStoppedEvent.
                    // A terminal DCP state is authoritative even without a final console message.
                    await InvokeAsync(async () =>
                    {
                        if (ReferenceEquals(_run, run))
                        {
                            await StopAndPublishAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        return true;
                    }, run.Cancellation.Token).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, DiagnosticPrefix + "Failed to observe tunnel process state.");
        }
    }

    private async Task ApplyLogsAsync(Run run, IReadOnlyList<LogLine> batch)
    {
        var changed = false;
        foreach (var entry in batch)
        {
            var content = entry.Content;
            var canWarn = true;
            if (TimestampParser.TryParseConsoleTimestamp(content, out var parsed))
            {
                if (parsed.Value.Timestamp < run.StartedAt)
                {
                    continue;
                }
                content = parsed.Value.ModifiedText;
                // Preparation hooks also write to this resource logger (for example certificate
                // configuration). They are not CLI output. Once DCP supplies the process start
                // time, exclude those entries even if they arrived in the same replayed batch.
                if (_notifications.TryGetCurrentState(_resource.Name, out var current)
                    && current.Snapshot.StartTimeStamp is { } startedAt
                    && startedAt >= run.StartedAt.UtcDateTime)
                {
                    if (parsed.Value.Timestamp.UtcDateTime < startedAt)
                    {
                        continue;
                    }
                }
                else
                {
                    canWarn = false;
                }
            }
            foreach (var line in DevTunnelOutputParser.SplitOutput(content))
            {
                // Resource logs also contain DCP messages and our own diagnostics. Never feed
                // diagnostics back into the parser, or a warning could recursively warn about itself.
                if (line.StartsWith("[sys]", StringComparison.Ordinal) || line.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var output = run.Parser.Parse(line);
                switch (output.Kind)
                {
                    case DevTunnelOutputParser.OutputKind.Port when run.ExpectedPorts.ContainsValue(output.Port):
                        run.Ports[output.Port] = output.Uri ?? throw new InvalidOperationException("A parsed tunnel port must include its URI.");
                        changed = true;
                        break;
                    case DevTunnelOutputParser.OutputKind.Ready:
                        run.Connected = true;
                        run.LocallyDisconnected = false;
                        run.HasReadyMessage = true;
                        changed = true;
                        break;
                    case DevTunnelOutputParser.OutputKind.Connected:
                        // "Restored" is emitted before the initial port block, but on reconnect
                        // previously observed ports can be reused.
                        run.Connected = run.HasReadyMessage || run.Ready.Task.IsCompletedSuccessfully;
                        run.LocallyDisconnected = false;
                        changed = true;
                        break;
                    case DevTunnelOutputParser.OutputKind.Disconnected:
                        run.Connected = false;
                        run.LocallyDisconnected = true;
                        changed = true;
                        break;
                    case DevTunnelOutputParser.OutputKind.Unrecognized when canWarn:
                        WarnUnrecognizedOutput(run);
                        break;
                }
            }
        }
        if (changed)
        {
            run.Revision++;
            run.Error = null;
            await PublishAsync(run).ConfigureAwait(false);
        }
    }

    private void WarnUnrecognizedOutput(Run run)
    {
        if (!IsCurrent(run) || run.WarnedAboutOutput)
        {
            return;
        }
        run.WarnedAboutOutput = true;
        _logger.LogWarning(DiagnosticPrefix + "Some devtunnel output was not recognized, or a complete readiness message has not arrived. Health checks will reconcile tunnel status. If status is incorrect, report an Aspire issue with the devtunnel version and relevant console output, after removing sensitive information.");
    }

    private async Task PublishAsync(Run run)
    {
        foreach (var (port, number) in run.ExpectedPorts)
        {
            port.LastKnownStatus = run.Ports.TryGetValue(number, out var knownUri) ? new(number, port.Options.Protocol!) { PortUri = knownUri } : null;
            if (run.Connected && run.Ports.TryGetValue(number, out var uri) && !_endpointEvents.ContainsKey(port))
            {
                if (port.TunnelEndpointAnnotation.AllocatedEndpoint is null)
                {
                    port.TunnelEndpointAnnotation.AllocatedEndpoint = new(port.TunnelEndpointAnnotation, uri.Host, 443);
                    // User endpoint callbacks can restart the tunnel. Execute them outside the
                    // serialized reader, and keep this one-time event independent of run lifetime.
                    _endpointEvents.Add(port, Task.Run(() => PublishEndpointEventAsync(port)));
                }
                else
                {
                    _allocatedEndpoints.Add(port);
                }
            }
        }

        var allPortsAvailable = run.ExpectedPorts.All(p => run.Ports.ContainsKey(p.Value) && _allocatedEndpoints.Contains(p.Key));
        var healthy = run.Connected && allPortsAvailable;
        var description = string.Format(CultureInfo.CurrentCulture,
            healthy ? MessageStrings.DevTunnelHostHealthy
                : run.LocallyDisconnected ? MessageStrings.DevTunnelHostDisconnected
                : MessageStrings.DevTunnelHostNotReady, _resource.TunnelId);
        if (!healthy && run.Error is { } error)
        {
            description = string.Format(CultureInfo.CurrentCulture, MessageStrings.DevTunnelUnhealthy_Error, _resource.TunnelId, error);
        }
        if (_endpointErrors.FirstOrDefault() is { Value: { } endpointError })
        {
            description = string.Format(CultureInfo.CurrentCulture, MessageStrings.DevTunnelUnhealthy_Error, _resource.TunnelId, endpointError.Message);
            run.Ready.TrySetException(endpointError);
        }

        await _notifications.PublishUpdateAsync(_resource, snapshot => WithConnectionHealth(snapshot, _resource.Name, healthy, description)).ConfigureAwait(false);
        foreach (var (port, number) in run.ExpectedPorts)
        {
            var available = run.Connected && run.Ports.ContainsKey(number) && _allocatedEndpoints.Contains(port);
            await DevTunnelsResourceBuilderExtensions.UpdatePortAsync(port, available, _services, run.Cancellation.Token).ConfigureAwait(false);
        }
        if (healthy && run.Ready.TrySetResult())
        {
            // Fetch access metadata promptly, but never block startup on those extra CLI calls.
            run.AccessTask = QueryAccessAsync(run);
        }
    }

    private async Task PublishEndpointEventAsync(DevTunnelPortResource port)
    {
        var cancellationToken = _stopping.Token;
        try
        {
            await _services.GetRequiredService<IDistributedApplicationEventing>()
                .PublishAsync(new ResourceEndpointsAllocatedEvent(port, _services), cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            await InvokeAsync(async () =>
            {
                _allocatedEndpoints.Add(port);
                if (_run is { } current && IsCurrent(current))
                {
                    await PublishAsync(current).ConfigureAwait(false);
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, DiagnosticPrefix + "Failed to publish endpoint allocation for tunnel port '{PortResource}'.", port.Name);
            try
            {
                await InvokeAsync(async () =>
                {
                    _endpointErrors[port] = ex;
                    if (_run is { } current && IsCurrent(current))
                    {
                        current.Ready.TrySetException(ex);
                        await PublishAsync(current).ConfigureAwait(false);
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    internal static CustomResourceSnapshot WithConnectionHealth(CustomResourceSnapshot snapshot, string name, bool healthy, string description)
    {
        var key = name + "-connection";
        return snapshot.WithHealthReports([
            .. snapshot.HealthReports.Where(r => r.Name != key),
            new(key, healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, description, null)
        ]);
    }

    private bool IsCurrent(Run run) =>
        ReferenceEquals(_run, run)
        && !run.Cancellation.IsCancellationRequested
        && (!_notifications.TryGetCurrentState(_resource.Name, out var current)
            || !KnownResourceStates.TerminalStates.Contains(current.Snapshot.State?.Text));

    private async Task StopRunAsync()
    {
        if (_run is not { } run)
        {
            return;
        }
        _run = null;
        await run.Cancellation.CancelAsync().ConfigureAwait(false);
        run.Ready.TrySetCanceled(run.Cancellation.Token);
        await Task.WhenAll(run.LogTask, run.StateTask, run.ReconciliationTask, run.AccessTask, run.LoginTask, run.InitialHealthTask ?? Task.CompletedTask).ConfigureAwait(false);
        run.Cancellation.Dispose();
    }

    private async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        T result = default!;
        await _operations.Writer.WriteAsync(new(async () => result = await action().ConfigureAwait(false), completion, cancellationToken), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ProcessAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                operation.CancellationToken.ThrowIfCancellationRequested();
                await operation.Action().ConfigureAwait(false);
                operation.Completion.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                operation.Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                operation.Completion.TrySetException(ex);
            }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        _operations.Writer.TryComplete();
        await _processing.ConfigureAwait(false);
        await StopRunAsync().ConfigureAwait(false);
        await Task.WhenAll(_endpointEvents.Values).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed record Operation(Func<Task> Action, TaskCompletionSource Completion, CancellationToken CancellationToken);

    private sealed class Run(string tunnelId, Dictionary<DevTunnelPortResource, int> expectedPorts, string friendlyTunnelId, DateTimeOffset startedAt, CancellationToken stoppingToken)
    {
        public string TunnelId { get; set; } = tunnelId;
        public Dictionary<DevTunnelPortResource, int> ExpectedPorts { get; } = expectedPorts;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DevTunnelOutputParser Parser { get; } = new(friendlyTunnelId);
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<int, Uri> Ports { get; } = [];
        public Task LogTask { get; set; } = Task.CompletedTask;
        public Task StateTask { get; set; } = Task.CompletedTask;
        public Task ReconciliationTask { get; set; } = Task.CompletedTask;
        public Task AccessTask { get; set; } = Task.CompletedTask;
        public Task LoginTask { get; set; } = Task.CompletedTask;
        public Task? InitialHealthTask { get; set; }
        public long Revision { get; set; }
        public bool Connected { get; set; }
        public bool HasReadyMessage { get; set; }
        public bool LocallyDisconnected { get; set; }
        public bool WarnedAboutOutput { get; set; }
        public string? Error { get; set; }
    }
}
