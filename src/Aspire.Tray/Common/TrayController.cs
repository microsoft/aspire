// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Tray;

internal sealed class TrayController : IAsyncDisposable
{
    private readonly IAppHostClient _client;
    private readonly ITraySavedStateStore _savedStateStore;
    private readonly int _recentAppHostLimit;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<AppHostId, StopOperation> _stops = [];
    private readonly Dictionary<string, StartOperation> _starts = new(TrayAppHostPath.Comparer);
    private HashSet<AppHostId> _lastLiveIds = [];
    private TraySavedState _savedState = TraySavedState.Empty;
    private AppHostSnapshot _snapshot = new([], DiscoveryState.Connecting);
    private TrayViewState _state = new(DiscoveryState.Connecting, [], "Connecting to Aspire...");
    private Task? _watcher;
    private Task? _disposeTask;
    private string? _actionError;
    private string? _savedStateError;
    private string? _pinProbeError;
    private bool _savedStateDirty;
    private bool _disposed;

    public TrayController(IAppHostClient client) : this(client, new MemoryTraySavedStateStore())
    {
    }

    public TrayController(IAppHostClient client, ITraySavedStateStore savedStateStore)
        : this(client, savedStateStore, TraySavedState.DefaultRecentAppHostLimit)
    {
    }

    public TrayController(IAppHostClient client, ITraySavedStateStore savedStateStore, int recentAppHostLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentAppHostLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recentAppHostLimit, TraySavedState.MaximumRecentAppHosts);
        _client = client;
        _savedStateStore = savedStateStore;
        _recentAppHostLimit = recentAppHostLimit;
        try
        {
            _savedState = savedStateStore.Load();
        }
        catch (Exception ex) when (IsSavedStateException(ex))
        {
            Console.Error.WriteLine($"Saved AppHost state could not be loaded ({ex.GetType().Name}).");
            _savedStateError = "Unable to load saved AppHosts. The saved file was left unchanged.";
        }
        var trimmed = _savedState.Trim(_recentAppHostLimit);
        if (!TrySaveStateLocked(trimmed))
        {
            // The configured limit also applies when storage is temporarily unavailable.
            // Keep the original file untouched and show the existing persistence error.
            _savedState = trimmed;
            _savedStateDirty = true;
        }
        PublishLocked();
    }

    // Raised on the publishing thread. Native frontends coalesce and post a main-thread refresh.
    public event Action? Changed;

    public TrayViewState State => Volatile.Read(ref _state);

    /// <summary>
    /// Gets whether the native frontend must confirm an AppHost stop.
    /// </summary>
    public bool ConfirmStop
    {
        get
        {
            lock (_gate)
            {
                return _savedState.ConfirmStop;
            }
        }
    }

    /// <summary>
    /// Persists the stop-confirmation preference before changing its in-memory value.
    /// </summary>
    public void SetConfirmStop(bool confirmStop)
        => UpdateSavedState(state => state with { ConfirmStop = confirmStop });

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

    public void SetPinned(string appHostPath, bool pinned)
    {
        var path = TrayAppHostPath.Normalize(appHostPath);
        UpdateSavedState(state => state.SetPinned(path, pinned));
    }

    public void ClearRecent() => UpdateSavedState(state => state.ClearRecent());

    public void RemoveRecent(string appHostPath)
    {
        var path = TrayAppHostPath.Normalize(appHostPath);
        UpdateSavedState(state => state.RemoveRecent(path));
    }

    /// <summary>
    /// Removes missing offline pins after discovery has established which paths are running.
    /// </summary>
    public void PruneMissingPinnedAppHosts()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = (_savedState, _savedStateError, _pinProbeError);
            PruneMissingPinsLocked();
            if (previous == (_savedState, _savedStateError, _pinProbeError))
            {
                return;
            }
            PublishLocked();
        }
        Changed?.Invoke();
    }

    private void UpdateSavedState(Func<TraySavedState, TraySavedState> update)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                PruneMissingPinsLocked();
                var next = update(_savedState);
                if (!TrySaveStateLocked(next))
                {
                    PublishLocked();
                    throw new InvalidOperationException(_savedStateError);
                }
                PruneMissingPinsLocked();
                _actionError = null;
                PublishLocked();
            }
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    public void RequestStart(string appHostPath)
    {
        var path = TrayAppHostPath.Normalize(appHostPath);
        try
        {
            lock (_gate)
            {
                try
                {
                    RequireStartablePathLocked(path);
                    if (_starts.TryGetValue(path, out var existing) && existing.BlocksStart)
                    {
                        throw new InvalidOperationException("A start request is already running for this AppHost. Wait for discovery.");
                    }
                    if (PruneMissingPinsLocked().Contains(path))
                    {
                        return;
                    }
                    TrayAppHostPath.RequireExistingFile(path);
                    RememberLocked([path]);
                    var operation = new StartOperation();
                    _starts[path] = operation;
                    _actionError = null;
                    operation.Task = Task.Run(() => StartAsync(path, operation));
                }
                finally
                {
                    PublishLocked();
                }
            }
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private void RequireStartablePathLocked(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshot.Discovery != DiscoveryState.Live)
        {
            throw new InvalidOperationException("AppHost discovery is disconnected. Wait for it to reconnect before starting.");
        }
        // Even a successfully stopped row remains a live identity until discovery removes it.
        // Never interpret stale discovery or unknown resource health as permission to start.
        if (_snapshot.AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(TrayAppHostPath.Normalize(host.AppHostPath), path)))
        {
            throw new InvalidOperationException("This AppHost is already listed. Wait for discovery before starting it again.");
        }
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
            await foreach (var snapshot in _client.WatchAsync(_shutdown.Token).ConfigureAwait(false))
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
            if (snapshot.Discovery == DiscoveryState.Live)
            {
                var arrivingPaths = snapshot.AppHosts.Where(host => !_lastLiveIds.Contains(host.Id))
                    .Select(host => TrayAppHostPath.Normalize(host.AppHostPath))
                    .Distinct(TrayAppHostPath.Comparer).ToArray();
                _lastLiveIds = snapshot.AppHosts.Select(host => host.Id).ToHashSet();
                _snapshot = snapshot;
                // Only real instance arrivals update history. Heartbeats and reconnects must
                // not restore a recent entry the user cleared while its AppHost was running.
                RememberLocked(arrivingPaths.Reverse());
                foreach (var host in snapshot.AppHosts)
                {
                    var path = TrayAppHostPath.Normalize(host.AppHostPath);
                    if (_starts.TryGetValue(path, out var start))
                    {
                        start.Discovered = true;
                        if (start.Task.IsCompleted)
                        {
                            _starts.Remove(path);
                        }
                    }
                }
                PruneMissingPinsLocked();
            }
            else
            {
                // A disconnected stream has no authority to declare existing rows stopped.
                _snapshot = _snapshot with { Discovery = snapshot.Discovery };
            }
            var liveIds = _snapshot.AppHosts.Select(host => host.Id).ToHashSet();
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
            result = await _client.StopAsync(id, _shutdown.Token).ConfigureAwait(false);
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

    private async Task StartAsync(string path, StartOperation operation)
    {
        StartResult result;
        try
        {
            Task<StartResult> command;
            lock (_gate)
            {
                // Discovery may have changed after the menu gesture but before this worker.
                RequireStartablePathLocked(path);
                TrayAppHostPath.RequireExistingFile(path);
                command = _client.StartAsync(path, _shutdown.Token);
            }
            result = await command.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine("The tray closed while waiting for start; the AppHost may still start.");
            return;
        }
        catch (FileNotFoundException)
        {
            result = new(StartOutcome.NotFound, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"The AppHost start command failed ({ex.GetType().Name}).");
            result = new(StartOutcome.Failed, null);
        }
        if (result.Outcome != StartOutcome.Started)
        {
            Console.Error.WriteLine($"The AppHost start command did not succeed ({result.Outcome}, exit {result.ExitCode}).");
        }
        lock (_gate)
        {
            operation.Result = result;
            if (_disposed)
            {
                return;
            }
            var removedPins = PruneMissingPinsLocked();
            if ((result.Outcome != StartOutcome.NotFound || !removedPins.Contains(path))
                && !_savedState.AppHosts.Any(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, path)))
            {
                _actionError = GetStartError(result);
            }
            PublishLocked();
        }
        Changed?.Invoke();
    }

    private HashSet<string> PruneMissingPinsLocked()
    {
        var missing = new HashSet<string>(TrayAppHostPath.Comparer);
        // Loading preferences alone cannot tell whether a source-deleted AppHost is still
        // running. Wait for the first live snapshot, and never prune during a disconnect.
        if (_snapshot.Discovery != DiscoveryState.Live)
        {
            return missing;
        }
        var protectedPaths = _snapshot.AppHosts.Select(host => TrayAppHostPath.Normalize(host.AppHostPath))
            .ToHashSet(TrayAppHostPath.Comparer);
        protectedPaths.UnionWith(_starts.Where(pair => pair.Value.BlocksStart).Select(pair => pair.Key));
        var probeFailed = false;
        foreach (var host in _savedState.AppHosts.Where(host => host.IsPinned && !protectedPaths.Contains(host.AppHostPath)))
        {
            try
            {
                if (TrayAppHostPath.IsMissing(host.AppHostPath))
                {
                    missing.Add(host.AppHostPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Console.Error.WriteLine($"A saved AppHost source path could not be checked ({ex.GetType().Name}).");
                probeFailed = true;
            }
        }
        _pinProbeError = probeFailed ? "Unable to check saved AppHost paths. Unavailable pins were kept." : null;
        if (missing.Count > 0)
        {
            // Forget both flags in one durable update, rather than unpinning into recents.
            // A failed write keeps the old in-memory state and its visible storage error.
            TrySaveStateLocked(_savedState.RemoveMissingPins(missing));
        }
        return missing;
    }

    private void RememberLocked(IEnumerable<string> paths)
    {
        var state = _savedState;
        foreach (var path in paths)
        {
            state = state.Remember(path, _recentAppHostLimit);
        }
        if (!TrySaveStateLocked(state))
        {
            // Discovery still works in memory after a storage failure, but its persistent
            // error stays visible and the store will not overwrite an unreadable file.
            _savedState = state;
            _savedStateDirty = true;
        }
    }

    private bool TrySaveStateLocked(TraySavedState state)
    {
        if (!_savedStateDirty && _savedState.ConfirmStop == state.ConfirmStop && _savedState.AppHosts.SequenceEqual(state.AppHosts))
        {
            return true;
        }
        try
        {
            _savedStateStore.Save(state);
            _savedState = state;
            _savedStateDirty = false;
            _savedStateError = null;
            return true;
        }
        catch (Exception ex) when (IsSavedStateException(ex))
        {
            Console.Error.WriteLine($"Saved AppHost state could not be written ({ex.GetType().Name}).");
            _savedStateError = "Unable to save AppHost history. The saved file was left unchanged.";
            return false;
        }
    }

    private static bool IsSavedStateException(Exception ex)
        => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;

    private void PublishLocked()
    {
        var live = _snapshot.Discovery == DiscoveryState.Live;
        var pins = _savedState.AppHosts.Where(host => host.IsPinned)
            .Select(host => host.AppHostPath).ToHashSet(TrayAppHostPath.Comparer);
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
                stopping, error)
            {
                IsPinned = pins.Contains(TrayAppHostPath.Normalize(host.AppHostPath)),
                IsRunning = !stopped,
                Health = live && !stopped ? host.Health : AppHostHealth.Unknown
            };
        }).ToList();
        var listedPaths = _snapshot.AppHosts.Select(host => TrayAppHostPath.Normalize(host.AppHostPath))
            .ToHashSet(TrayAppHostPath.Comparer);
        foreach (var saved in _savedState.AppHosts.Where(host => host.IsPinned && !listedPaths.Contains(host.AppHostPath)))
        {
            rows.Add(CreateSavedMenuItem(saved, live));
            listedPaths.Add(saved.AppHostPath);
        }
        var recent = _savedState.AppHosts.Where(host => host.IsRecent && !listedPaths.Contains(host.AppHostPath)
                && (!_starts.TryGetValue(host.AppHostPath, out var operation)
                    || operation.Result?.Outcome != StartOutcome.Started || operation.Discovered))
            .Select(host => CreateSavedMenuItem(host, live)).ToArray();
        var pending = rows.Count(row => row.IsStopping);
        var starting = _starts.Values.Count(operation => operation.IsPending);
        var failures = rows.Concat(recent).Count(row => row.Error is not null);
        var running = rows.Count(row => row.IsRunning);
        var status = _snapshot.Discovery switch
        {
            _ when _savedStateError is not null => _savedStateError,
            _ when _pinProbeError is not null => _pinProbeError,
            DiscoveryState.Connecting => "Connecting to Aspire...",
            DiscoveryState.Disconnected => "Discovery unavailable. Reconnecting...",
            DiscoveryState.Incompatible => "Incompatible CLI. Use the matching Aspire build.",
            DiscoveryState.LimitExceeded => "Discovery limit exceeded. Too many AppHosts or too much metadata.",
            _ when _actionError is not null => _actionError,
            _ when failures > 0 => $"{failures} AppHost{(failures == 1 ? "" : "s")} need{(failures == 1 ? "s" : "")} attention",
            _ when pending > 0 => $"Stopping {pending} AppHost{(pending == 1 ? "" : "s")}...",
            _ when starting > 0 => $"Starting {starting} AppHost{(starting == 1 ? "" : "s")}...",
            _ => running switch { 0 => "No AppHosts running", 1 => "1 AppHost", _ => $"{running} AppHosts" }
        };
        Volatile.Write(ref _state, new(_snapshot.Discovery, rows.ToArray(), status)
        {
            RecentAppHosts = recent,
            CanClearRecent = _savedState.AppHosts.Any(host => host.IsRecent),
            HasActiveAppHosts = running > 0 || starting > 0,
            ShowStatus = rows.Count == 0 || !live || _actionError is not null || _savedStateError is not null
                || _pinProbeError is not null || failures > 0
        });
    }

    private AppHostMenuItem CreateSavedMenuItem(SavedAppHost saved, bool live)
    {
        var host = new AppHostInfo(saved.AppHostPath, 0, null);
        _starts.TryGetValue(saved.AppHostPath, out var start);
        var starting = start?.IsPending == true;
        var error = GetStartError(start?.Result);
        var subtitle = error ?? (starting ? "Starting AppHost; waiting for discovery..."
            : saved.IsPinned || File.Exists(saved.AppHostPath) ? "Stopped" : "AppHost source file not found");
        return new(host.Id, AppHostPresentation.GetTitle(host), subtitle, AppHostPresentation.GetDisplayName(host),
            false, false, false, error)
        {
            IsPinned = saved.IsPinned,
            CanStart = live && start?.BlocksStart != true,
            IsStarting = starting,
            IsRunning = false,
            Health = AppHostHealth.Unknown
        };
    }

    private static string? GetStartError(StartResult? result) => result?.Outcome switch
    {
        null or StartOutcome.Started => null,
        StartOutcome.NotFound => "The AppHost source file no longer exists.",
        StartOutcome.TimedOut => "Start timed out. The AppHost may still start; wait for discovery before retrying.",
        _ => $"Unable to start AppHost{(result.ExitCode is int code ? $" (CLI exit {code})" : "")}."
    };

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
        tasks.AddRange(_starts.Values.Select(operation => operation.Task));
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

    private sealed class StartOperation
    {
        public Task Task { get; set; } = Task.CompletedTask;
        public StartResult? Result { get; set; }
        public bool Discovered { get; set; }
        public bool IsPending => Result is null || (Result.Outcome == StartOutcome.Started && !Discovered);
        public bool BlocksStart => IsPending || (Result?.Outcome == StartOutcome.TimedOut && !Discovered);
    }
}
