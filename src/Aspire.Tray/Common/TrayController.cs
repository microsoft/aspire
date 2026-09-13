// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed class TrayController(IAppHostClient client) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<AppHostId, StopOperation> _stops = [];
    private AppHostSnapshot _snapshot = new([], DiscoveryState.Connecting);
    private TrayViewState _state = new(DiscoveryState.Connecting, [], "Connecting to Aspire...");
    private Task? _watcher;
    private Task? _disposeTask;
    private string? _actionError;
    private bool _disposed;

    // Raised on the publishing thread. Native frontends coalesce and post a main-thread refresh.
    public event Action? Changed;

    public TrayViewState State => Volatile.Read(ref _state);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                throw new InvalidOperationException("The tray controller is already started.");
            }
            _watcher = Task.Run(WatchAsync);
        }
    }

    public AppHostInfo RequireLiveInstance(AppHostId id)
    {
        lock (_gate)
        {
            return RequireLiveInstanceLocked(id);
        }
    }

    public Uri GetDashboardUri(AppHostId id)
    {
        Uri uri;
        lock (_gate)
        {
            uri = RequireLiveInstanceLocked(id).DashboardUri
                ?? throw new InvalidOperationException("The selected dashboard is unavailable.");
            _actionError = null;
            PublishLocked();
        }
        Changed?.Invoke();
        return uri;
    }

    public void RequestStop(AppHostId id)
    {
        lock (_gate)
        {
            // Confirmation is owned by the native frontend. Revalidate here because discovery
            // can change while that modal dialog is open, including reuse of a process ID.
            var host = RequireLiveInstanceLocked(id);
            if (host.ProcessStartTimeUnixMilliseconds is not > 0)
            {
                throw new InvalidOperationException("The process identity is unavailable. Stop is disabled.");
            }
            if (_stops.TryGetValue(id, out var existing) && existing.Result is null)
            {
                throw new InvalidOperationException("A stop request is already running for this AppHost.");
            }

            var operation = new StopOperation();
            _stops[id] = operation;
            _actionError = null;
            operation.Task = Task.Run(() => StopAsync(id, operation));
            PublishLocked();
        }
        Changed?.Invoke();
    }

    public void ReportActionError(string message)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _actionError = message;
            PublishLocked();
        }
        Changed?.Invoke();
    }

    private AppHostInfo RequireLiveInstanceLocked(AppHostId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshot.Discovery != DiscoveryState.Live)
        {
            throw new InvalidOperationException("AppHost discovery is disconnected. Wait for it to reconnect.");
        }
        if (_stops.TryGetValue(id, out var operation) && operation.Result?.Outcome == StopOutcome.Stopped)
        {
            throw new InvalidOperationException("The selected AppHost instance is no longer running.");
        }
        return _snapshot.AppHosts.SingleOrDefault(host => host.Id == id)
            ?? throw new InvalidOperationException("The selected AppHost instance is no longer running.");
    }

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var snapshot in client.WatchAsync(_shutdown.Token).ConfigureAwait(false))
            {
                SetSnapshot(snapshot);
            }
            if (!_shutdown.IsCancellationRequested && State.Discovery is not (DiscoveryState.Incompatible or DiscoveryState.LimitExceeded))
            {
                Console.Error.WriteLine("AppHost discovery ended unexpectedly.");
                SetDisconnected();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // This is the background-worker boundary. Payloads and exception messages may
            // contain dashboard login tokens; only expose the failure category.
            Console.Error.WriteLine($"AppHost discovery failed ({ex.GetType().Name}).");
            SetDisconnected();
        }
    }

    private void SetDisconnected()
    {
        AppHostSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot with { Discovery = DiscoveryState.Disconnected };
        }
        SetSnapshot(snapshot);
    }

    private void SetSnapshot(AppHostSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _snapshot = snapshot;
            var liveIds = snapshot.AppHosts.Select(host => host.Id).ToHashSet();
            foreach (var (id, operation) in _stops.ToArray())
            {
                if (operation.Task.IsCompleted && !liveIds.Contains(id))
                {
                    _stops.Remove(id);
                }
            }
            PublishLocked();
        }
        Changed?.Invoke();
    }

    private async Task StopAsync(AppHostId id, StopOperation operation)
    {
        StopResult result;
        try
        {
            result = await client.StopAsync(id, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine("The tray closed while waiting for stop; the AppHost shutdown may still complete.");
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"The exact-instance stop command failed ({ex.GetType().Name}).");
            result = new(StopOutcome.Failed, null);
        }

        if (result.Outcome != StopOutcome.Stopped)
        {
            Console.Error.WriteLine($"The exact-instance stop command did not succeed ({result.Outcome}, exit {result.ExitCode}).");
        }
        lock (_gate)
        {
            operation.Result = result;
            if (_disposed)
            {
                return;
            }
            PublishLocked();
        }
        Changed?.Invoke();
    }

    private void PublishLocked()
    {
        var live = _snapshot.Discovery == DiscoveryState.Live;
        var rows = _snapshot.AppHosts.Select(host =>
        {
            _stops.TryGetValue(host.Id, out var stop);
            var stopping = stop is { Result: null };
            var stopped = stop?.Result?.Outcome == StopOutcome.Stopped;
            var error = GetStopError(stop?.Result)
                ?? (host.ProcessStartTimeUnixMilliseconds is null ? "Process identity unavailable; Stop is disabled." : null);
            var subtitle = error ?? (stopping ? "Stopping AppHost..."
                : stopped ? "Stopped; waiting for discovery." : AppHostPresentation.GetSubtitle(host));
            return new AppHostMenuItem(host.Id, AppHostPresentation.GetTitle(host), subtitle,
                AppHostPresentation.GetDisplayName(host),
                live && !stopped && host.DashboardUri is not null,
                live && !stopped && !stopping && host.ProcessStartTimeUnixMilliseconds is > 0,
                stopping, error);
        }).ToArray();
        var pending = rows.Count(row => row.IsStopping);
        var failures = rows.Count(row => row.Error is not null);
        var status = _snapshot.Discovery switch
        {
            DiscoveryState.Connecting => "Connecting to Aspire...",
            DiscoveryState.Disconnected => "Discovery unavailable. Reconnecting...",
            DiscoveryState.Incompatible => "Incompatible CLI. Use the matching Aspire build.",
            DiscoveryState.LimitExceeded => "Discovery limit exceeded. Too many AppHosts or too much metadata.",
            _ when _actionError is not null => _actionError,
            _ when pending > 0 => $"Stopping {pending} AppHost{(pending == 1 ? "" : "s")}...",
            _ when failures > 0 => $"{failures} AppHost{(failures == 1 ? "" : "s")} need{(failures == 1 ? "s" : "")} attention",
            _ => rows.Length switch { 0 => "No AppHosts running", 1 => "1 AppHost", _ => $"{rows.Length} AppHosts" }
        };
        Volatile.Write(ref _state, new(_snapshot.Discovery, rows, status));
    }

    private static string? GetStopError(StopResult? result) => result?.Outcome switch
    {
        null or StopOutcome.Stopped => null,
        StopOutcome.NotFound => "This AppHost is no longer running.",
        StopOutcome.Ambiguous => "The CLI could not identify a unique AppHost.",
        StopOutcome.IdentityMismatch => "The AppHost process changed. Refresh before stopping it.",
        StopOutcome.IdentityUnavailable => "The process identity could not be verified.",
        StopOutcome.TimedOut => "Stop timed out. The shutdown may still complete.",
        StopOutcome.Incompatible => "Incompatible CLI stop response. Use the matching Aspire build.",
        _ => $"Unable to stop AppHost{(result.ExitCode is int code ? $" (CLI exit {code})" : "")}."
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        var tasks = _stops.Values.Select(operation => operation.Task).ToList();
        if (_watcher is not null)
        {
            tasks.Add(_watcher);
        }
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private sealed class StopOperation
    {
        public Task Task { get; set; } = Task.CompletedTask;
        public StopResult? Result { get; set; }
    }
}
