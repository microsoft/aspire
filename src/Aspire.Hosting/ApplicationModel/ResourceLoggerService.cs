// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A service that provides loggers for resources to write to.
/// </summary>
public class ResourceLoggerService : IDisposable
{
    // Internal for testing.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    private readonly ConcurrentDictionary<string, ResourceLoggerState> _loggers = new();
    private readonly CancellationTokenSource _disposing = new();
    private int _disposed;
    private IConsoleLogsService _consoleLogsService = new FakeConsoleLogsService();
    private Action<(string, ResourceLoggerState)>? _loggerAdded;
    private event Action<(string, ResourceLoggerState)> LoggerAdded
    {
        add
        {
            _loggerAdded += value;

            foreach (var logger in _loggers)
            {
                value((logger.Key, logger.Value));
            }
        }
        remove
        {
            _loggerAdded -= value;
        }
    }

    /// <summary>
    /// Gets the logger for the resource to write to.
    /// </summary>
    /// <param name="resource">The resource name</param>
    /// <returns>An <see cref="ILogger"/> which represents the resource.</returns>
    public ILogger GetLogger(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var resourceNames = resource.GetResolvedResourceNames();
        if (resourceNames.Length > 1)
        {
            // If a resource has multiple replicas then return a composite logger that writes to multiple.
            var loggers = new List<ILogger>();
            foreach (var resourceName in resourceNames)
            {
                loggers.Add(GetResourceLoggerState(resourceName).Logger);
            }

            return new CompositeLogger(loggers);
        }
        else
        {
            return GetResourceLoggerState(resourceNames[0]).Logger;
        }
    }

    private sealed class CompositeLogger(List<ILogger> innerLoggers) : ILogger
    {
        private readonly List<ILogger> _innerLoggers = innerLoggers;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var scopes = new List<IDisposable>();
            foreach (var logger in _innerLoggers)
            {
                if (logger.BeginScope(state) is { } scope)
                {
                    scopes.Add(scope);
                }
            }

            if (scopes.Count == 0)
            {
                return null;
            }
            else if (scopes.Count == 1)
            {
                return scopes[0];
            }
            else
            {
                return new CompositeDisposable(scopes);
            }
        }

        private sealed class CompositeDisposable(List<IDisposable> disposables) : IDisposable
        {
            private readonly List<IDisposable> _disposables = disposables;

            public void Dispose()
            {
                foreach (var disposable in _disposables)
                {
                    disposable.Dispose();
                }
            }
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            // All loggers have the same log level.
            return _innerLoggers[0].IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            foreach (var logger in _innerLoggers)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    /// <summary>
    /// Gets the logger for the resource to write to.
    /// </summary>
    /// <param name="resourceName">The name of the resource from the Aspire application model.</param>
    /// <returns>An <see cref="ILogger"/> which represents the named resource.</returns>
    public ILogger GetLogger(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);

        return GetResourceLoggerState(resourceName).Logger;
    }

    /// <summary>
    /// Get all logs for a resource. This will return all logs that have been written to the log stream for the resource and then complete.
    /// </summary>
    /// <param name="resource">The resource to get all logs for.</param>
    /// <returns>An async enumerable that returns all logs that have been written to the log stream and then completes.</returns>
    public IAsyncEnumerable<IReadOnlyList<LogLine>> GetAllAsync(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var resourceNames = resource.GetResolvedResourceNames();
        if (resourceNames.Length > 1)
        {
            return CombineMultipleAsync(resourceNames, GetAllAsync);
        }
        else
        {
            return GetAllAsync(resourceNames[0]);
        }
    }

    /// <summary>
    /// Watch for changes to the log stream for a resource.
    /// </summary>
    /// <param name="resource">The resource to watch for logs.</param>
    /// <returns>An async enumerable that returns the logs as they are written.</returns>
    public IAsyncEnumerable<IReadOnlyList<LogLine>> WatchAsync(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var resourceNames = resource.GetResolvedResourceNames();
        if (resourceNames.Length > 1)
        {
            return CombineMultipleAsync(resourceNames, WatchAsync);
        }
        else
        {
            return WatchAsync(resourceNames[0]);
        }
    }

    /// <summary>
    /// Get all logs for a resource. This will return all logs that have been written to the log stream for the resource and then complete.
    /// </summary>
    /// <param name="resourceName">The resource name</param>
    /// <returns>An async enumerable that returns all logs that have been written to the log stream and then completes.</returns>
    public IAsyncEnumerable<IReadOnlyList<LogLine>> GetAllAsync(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);

        return GetResourceLoggerState(resourceName).GetAllAsync(_consoleLogsService);
    }

    /// <summary>
    /// Watch for changes to the log stream for a resource.
    /// </summary>
    /// <param name="resourceName">The resource name</param>
    /// <returns>An async enumerable that returns the logs as they are written.</returns>
    public IAsyncEnumerable<IReadOnlyList<LogLine>> WatchAsync(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);

        return GetResourceLoggerState(resourceName).WatchAsync();
    }

    internal IDisposable Subscribe(string resourceName, Action<IReadOnlyList<LogLine>> onLogs)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(onLogs);

        return GetResourceLoggerState(resourceName).Subscribe(onLogs);
    }

    internal Task WaitForCompletionAsync(string resourceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceName);

        return GetResourceLoggerState(resourceName).WaitForCompletionAsync(cancellationToken);
    }

    /// <summary>
    /// Watch for subscribers to the log stream for a resource.
    /// </summary>
    /// <returns>
    /// An async enumerable that returns when the first subscriber is added to a log,
    /// or when the last subscriber is removed.
    /// </returns>
    public async IAsyncEnumerable<LogSubscriber> WatchAnySubscribersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<LogSubscriber>();
        var subscribedStates = new List<(ResourceLoggerState State, Action<bool> Handler)>();

        // Create a linked token that cancels when either the service is disposing or the caller cancels.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token, cancellationToken);

        void OnLoggerAdded((string Name, ResourceLoggerState State) loggerItem)
        {
            var (name, state) = loggerItem;

            Action<bool> handler = (hasSubscribers) =>
            {
                channel.Writer.TryWrite(new(name, hasSubscribers));
            };

            state.OnSubscribersChanged += handler;
            subscribedStates.Add((state, handler));
        }

        try
        {
            LoggerAdded += OnLoggerAdded;

            await foreach (var entry in channel.Reader.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            LoggerAdded -= OnLoggerAdded;

            // Unsubscribe from all OnSubscribersChanged events to prevent memory leaks
            foreach (var (state, handler) in subscribedStates)
            {
                state.OnSubscribersChanged -= handler;
            }

            channel.Writer.Complete();
        }
    }

    /// <summary>
    /// Completes the log stream for the resource.
    /// </summary>
    /// <param name="resource">The <see cref="IResource"/>.</param>
    public void Complete(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var resourceNames = resource.GetResolvedResourceNames();
        foreach (var resourceName in resourceNames)
        {
            if (_loggers.TryGetValue(resourceName, out var logger))
            {
                logger.Complete();
            }
        }
    }

    /// <summary>
    /// Completes the log stream for the resource.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    public void Complete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_loggers.TryGetValue(name, out var logger))
        {
            logger.Complete();
        }
    }

    /// <summary>
    /// Clears the log stream's backlog for the resource.
    /// </summary>
    public void ClearBacklog(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);

        if (_loggers.TryGetValue(resourceName, out var logger))
        {
            logger.ClearBacklog();
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<LogLine>> CombineMultipleAsync(string[] resourceNames, Func<string, IAsyncEnumerable<IReadOnlyList<LogLine>>> fetch)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<LogLine>>();
        var readTasks = resourceNames.Select(async (name) =>
        {
            await foreach (var logLines in fetch(name).ConfigureAwait(false))
            {
                channel.Writer.TryWrite(logLines);
            }
        });

        var completionTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(readTasks).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            yield return item;
        }

        await completionTask.ConfigureAwait(false);
    }

    // Internal for testing.
    internal ResourceLoggerState GetResourceLoggerState(string resourceName) =>
        _loggers.GetOrAdd(resourceName, (name, context) =>
        {
            var state = new ResourceLoggerState(name, TimeProvider);
            context._loggerAdded?.Invoke((name, state));
            return state;
        },
        this);

    internal bool HasActiveSubscribers(string resourceName)
    {
        return _loggers.TryGetValue(resourceName, out var logger) && logger.HasActiveSubscribers;
    }

    internal void AddLogEntries(string resourceName, IReadOnlyList<LogEntry> logEntries, bool inMemorySource, bool skipExisting)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(logEntries);

        GetResourceLoggerState(resourceName).AddLogs(logEntries, inMemorySource, skipExisting);
    }

    internal Dictionary<string, ResourceLoggerState> Loggers => _loggers.ToDictionary();

    /// <summary>
    /// A logger for the resource to write to.
    /// </summary>
    internal sealed class ResourceLoggerState
    {
        private const int MaxLogCount = 10_000;

        private readonly ResourceLogger _logger;
        private readonly CancellationTokenSource _logStreamCts = new();
        private readonly TaskCompletionSource _logStreamCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();

        private readonly CircularBuffer<LogEntry> _inMemoryEntries = new(MaxLogCount);
        private readonly LogEntries _backlog = new(MaxLogCount) { BaseLineNumber = 0 };
        private readonly string _name;
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// Creates a new <see cref="ResourceLoggerState"/>.
        /// </summary>
        public ResourceLoggerState(string name, TimeProvider timeProvider)
        {
            _logger = new ResourceLogger(this);
            _name = name;
            _timeProvider = timeProvider;
        }

        private Action<bool>? _onSubscribersChanged;

        // Internal test hook for pausing synchronous Subscribe after it has attached its callback
        // but before it delivers the backlog. This makes the backlog/live transition deterministic
        // without sleeps.
        internal Action? SynchronousSubscribeRegistered { get; set; }

        public event Action<bool> OnSubscribersChanged
        {
            add
            {
                _onSubscribersChanged += value;

                var hasSubscribers = false;

                lock (_lock)
                {
                    if (_onNewLog is not null) // we have subscribers
                    {
                        hasSubscribers = true;
                    }
                }

                if (hasSubscribers)
                {
                    value(hasSubscribers);
                }
            }
            remove
            {
                _onSubscribersChanged -= value;
            }
        }

        public async IAsyncEnumerable<IReadOnlyList<LogLine>> GetAllAsync(IConsoleLogsService consoleLogsService, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var consoleLogsEnumerable = consoleLogsService.GetAllLogsAsync(_name, cancellationToken);

            List<LogEntry> inMemoryEntries;
            lock (_lock)
            {
                inMemoryEntries = _inMemoryEntries.ToList();
            }

            var lineNumber = 0;
            yield return CreateLogLines(ref lineNumber, inMemoryEntries);

            await foreach (var item in consoleLogsEnumerable.ConfigureAwait(false))
            {
                yield return CreateLogLines(ref lineNumber, item);
            }
        }

        /// <summary>
        /// Watch for changes to the log stream for a resource.
        /// </summary>
        /// <returns>The log stream for the resource.</returns>
        public async IAsyncEnumerable<IReadOnlyList<LogLine>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Line number always restarts from 1 when watching logs.
            // Note that this will need to be improved if the log source (DCP) is changed to return a maximum number of lines.
            var lineNumber = 1;
            var channel = Channel.CreateUnbounded<LogEntry>();

            using var _ = _logStreamCts.Token.Register(() => channel.Writer.TryComplete());

            // No need to lock in the log method because TryWrite/TryComplete are already thread safe.
            void Log(LogEntry log) => channel.Writer.TryWrite(log);

            LogEntry[] backlogSnapshot;
            lock (_lock)
            {
                // If there are no subscribers then the backlog must be empty. Populate it with any in-memory logs.
                if (!HasSubscribers)
                {
                    Debug.Assert(_backlog.EntriesCount == 0, "The backlog should be empty if there are no subscribers.");

                    // Populate backlog with in-memory log messages on first subscription.
                    foreach (var logEntry in _inMemoryEntries)
                    {
                        _backlog.InsertSorted(logEntry);
                    }
                }

                backlogSnapshot = GetBacklogSnapshot();
                OnNewLog += Log;
            }

            try
            {
                if (backlogSnapshot.Length > 0)
                {
                    yield return CreateLogLines(ref lineNumber, backlogSnapshot);
                }

                await foreach (var entry in channel.GetBatchesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    yield return CreateLogLines(ref lineNumber, entry);
                }
            }
            finally
            {
                lock (_lock)
                {
                    OnNewLog -= Log;
                    channel.Writer.TryComplete();
                }
            }
        }

        public IDisposable Subscribe(Action<IReadOnlyList<LogLine>> onLogs)
        {
            // This is not a replacement for WatchAsync. WatchAsync intentionally decouples
            // producers and consumers through a channel, which is the right shape for dashboard
            // and backchannel consumers. This path is for ResourceLoggerForwarderService, which
            // forwards resource logs into host ILogger when resource logging is enabled. That
            // forwarder needs stronger ordering: after DCP flushes logs for a terminal state,
            // those logs must reach ILogger before ResourceNotificationService publishes the
            // terminal state that unblocks WaitForResourceAsync. Subscribe provides that
            // producer-thread delivery once the backlog has been drained.

            // Line number always restarts from 1 when watching logs.
            var lineNumber = 1;
            var subscriberLock = new object();
            var backlogDelivered = false;

            // Logs can arrive after we attach OnNewLog but before the backlog has been delivered.
            // Queue those entries so the subscriber sees backlog first without dropping live logs
            // from that small transition window.
            var pendingLiveEntries = new List<LogEntry>();

            void Log(LogEntry log)
            {
                if (_logStreamCts.IsCancellationRequested)
                {
                    return;
                }

                IReadOnlyList<LogLine>? logLines = null;

                lock (subscriberLock)
                {
                    if (!backlogDelivered)
                    {
                        pendingLiveEntries.Add(log);
                        return;
                    }

                    logLines = CreateLogLines(ref lineNumber, [log]);
                }

                onLogs(logLines);
            }

            LogEntry[] backlogSnapshot;
            lock (_lock)
            {
                // If there are no subscribers then the backlog must be empty. Populate it with any in-memory logs.
                if (!HasSubscribers)
                {
                    Debug.Assert(_backlog.EntriesCount == 0, "The backlog should be empty if there are no subscribers.");

                    // Populate backlog with in-memory log messages on first subscription.
                    foreach (var logEntry in _inMemoryEntries)
                    {
                        _backlog.InsertSorted(logEntry);
                    }
                }

                backlogSnapshot = GetBacklogSnapshot();
                OnNewLog += Log;
            }

            var subscription = new Subscription(this, Log);

            try
            {
                SynchronousSubscribeRegistered?.Invoke();

                var batches = new List<IReadOnlyList<LogLine>>();
                if (backlogSnapshot.Length > 0)
                {
                    batches.Add(CreateLogLines(ref lineNumber, backlogSnapshot));
                }

                // Drain the backlog and any live entries that arrived during backlog delivery before
                // returning the subscription. After this point, future AddLogs calls invoke onLogs
                // synchronously from the producer's thread.
                while (true)
                {
                    foreach (var batch in batches)
                    {
                        onLogs(batch);
                    }

                    lock (subscriberLock)
                    {
                        if (pendingLiveEntries.Count == 0)
                        {
                            backlogDelivered = true;
                            break;
                        }

                        batches = [CreateLogLines(ref lineNumber, pendingLiveEntries)];
                        pendingLiveEntries.Clear();
                    }
                }
            }
            catch
            {
                subscription.Dispose();
                throw;
            }

            return subscription;
        }

        public Task WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            return _logStreamCompletion.Task.WaitAsync(cancellationToken);
        }

        private bool HasSubscribers
        {
            get
            {
                Debug.Assert(Monitor.IsEntered(_lock));
                return _onNewLog != null;
            }
        }

        internal bool HasActiveSubscribers
        {
            get
            {
                lock (_lock)
                {
                    // Completed streams should not trigger a terminal-state snapshot flush. The
                    // subscriber might still be disposing, but no more logs should be delivered.
                    return !_logStreamCts.IsCancellationRequested && HasSubscribers;
                }
            }
        }

        // This provides the fan out to multiple subscribers.
        private Action<LogEntry>? _onNewLog;
        private event Action<LogEntry> OnNewLog
        {
            add
            {
                Debug.Assert(Monitor.IsEntered(_lock));

                // When this is the first subscriber, raise event so WatchAnySubscribersAsync publishes an update.
                // Is this the first subscriber?
                var raiseSubscribersChanged = _onNewLog is null;

                _onNewLog += value;

                if (raiseSubscribersChanged)
                {
                    _onSubscribersChanged?.Invoke(true);
                }
            }
            remove
            {
                Debug.Assert(Monitor.IsEntered(_lock));

                _onNewLog -= value;

                // When there are no more subscribers, raise event so WatchAnySubscribersAsync publishes an update.
                // Is this the last subscriber?
                var raiseSubscribersChanged = _onNewLog is null;
                if (raiseSubscribersChanged)
                {
                    // Clear backlog immediately.
                    // Avoids a race between message being subscription changed notification eventually clearing the
                    // logs and someone else watching logs and getting the backlog + complete replay off all logs.
                    ClearBacklog();

                    _onSubscribersChanged?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// The logger for the resource to write to. This will write updates to the live log stream for this resource.
        /// </summary>
        public ILogger Logger => _logger;

        /// <summary>
        /// Close the log stream for the resource. Future subscribers will not receive any updates and will complete immediately.
        /// </summary>
        public void Complete()
        {
            // REVIEW: Do we clean up the backlog?
            _logStreamCts.Cancel();
            _logStreamCompletion.TrySetResult();
        }

        public void ClearBacklog()
        {
            lock (_lock)
            {
                _backlog.Clear(keepActivePauseEntries: false);
                _backlog.BaseLineNumber = 0;
            }
        }

        internal LogEntry[] GetBacklogSnapshot()
        {
            lock (_lock)
            {
                return [.. _backlog.GetEntries()];
            }
        }

        public void AddLog(LogEntry logEntry, bool inMemorySource)
        {
            lock (_lock)
            {
                // Only add logs into the backlog if there are subscribers. If there aren't subscribers then
                // logs are replayed into this collection from various sources (DCP, in-memory).
                if (HasSubscribers)
                {
                    _backlog.InsertSorted(logEntry);
                }

                // Keep replayable logs in their own collection. These logs are replayed into
                // the backlog when a log watch starts without fetching them from an external source.
                if (inMemorySource)
                {
                    _inMemoryEntries.Add(logEntry);
                }
            }

            _onNewLog?.Invoke(logEntry);
        }

        public void AddLogs(IReadOnlyList<LogEntry> logEntries, bool inMemorySource, bool skipExisting)
        {
            if (logEntries.Count == 0)
            {
                return;
            }

            List<LogEntry>? addedEntries = null;
            lock (_lock)
            {
                Dictionary<LogEntryKey, int>? existingLogCounts = null;
                if (skipExisting)
                {
                    // Terminal-state snapshots and the normal follow stream can overlap. Use
                    // occurrence counts instead of a set so repeated identical log lines are
                    // preserved while only overlapping copies from the later source are skipped.
                    var existingEntries = HasSubscribers ? _backlog.GetEntries() : _inMemoryEntries;
                    existingLogCounts = [];

                    foreach (var existingEntry in existingEntries)
                    {
                        IncrementCount(existingLogCounts, LogEntryKey.Create(existingEntry));
                    }
                }

                foreach (var logEntry in logEntries)
                {
                    if (existingLogCounts is not null)
                    {
                        var key = LogEntryKey.Create(logEntry);
                        if (existingLogCounts.TryGetValue(key, out var count) && count > 0)
                        {
                            existingLogCounts[key] = count - 1;
                            continue;
                        }
                    }

                    addedEntries ??= [];

                    // Only add logs into the backlog if there are subscribers. If there aren't subscribers then
                    // logs are replayed into this collection from various sources (DCP, in-memory).
                    if (HasSubscribers)
                    {
                        _backlog.InsertSorted(logEntry);
                    }

                    // Keep replayable logs in their own collection. These logs are replayed into
                    // the backlog when a log watch starts without fetching them from an external source.
                    if (inMemorySource)
                    {
                        _inMemoryEntries.Add(logEntry);
                    }

                    addedEntries.Add(logEntry);
                }
            }

            if (addedEntries is null)
            {
                return;
            }

            foreach (var logEntry in addedEntries)
            {
                _onNewLog?.Invoke(logEntry);
            }

            static void IncrementCount(Dictionary<LogEntryKey, int> counts, LogEntryKey key)
            {
                counts.TryGetValue(key, out var count);
                counts[key] = count + 1;
            }
        }

        private sealed class Subscription(ResourceLoggerState loggerState, Action<LogEntry> log) : IDisposable
        {
            public void Dispose()
            {
                lock (loggerState._lock)
                {
                    loggerState.OnNewLog -= log;
                }
            }
        }

        private sealed class ResourceLogger(ResourceLoggerState loggerState) : ILogger
        {
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;

            bool ILogger.IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (loggerState._logStreamCts.IsCancellationRequested)
                {
                    // Noop if logging after completing the stream
                    return;
                }

                var logTime = loggerState._timeProvider.GetUtcNow().UtcDateTime;

                var logMessage = formatter(state, exception) + (exception is null ? "" : $"\n{exception}");
                var isErrorMessage = logLevel >= LogLevel.Error;

                loggerState.AddLog(LogEntry.Create(logTime, logMessage, isErrorMessage), inMemorySource: true);
            }
        }
    }

    private static LogLine[] CreateLogLines(ref int lineNumber, IReadOnlyList<LogEntry> entries)
    {
        var logs = new LogLine[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var content = entry.Content ?? string.Empty;
            if (entry.Timestamp != null)
            {
                content = entry.Timestamp.Value.ToString(KnownFormats.ConsoleLogsTimestampFormat, CultureInfo.InvariantCulture) + " " + content;
            }

            logs[i] = new LogLine(lineNumber, content, entry.Type == LogEntryType.Error);
            lineNumber++;
        }

        return logs;
    }

    internal void SetConsoleLogsService(IConsoleLogsService consoleLogsService)
    {
        _consoleLogsService = consoleLogsService;
    }

    /// <summary>
    /// Disposes the service and completes all log streams.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Complete all loggers to signal that no more logs will be written.
        foreach (var logger in _loggers)
        {
            logger.Value.Complete();
        }

        // Cancel but don't dispose - other methods may still be accessing _disposing.Token
        // The CTS will be garbage collected with the service.
        _disposing.Cancel();
    }

    private sealed class FakeConsoleLogsService : IConsoleLogsService
    {
        public IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(string resourceName, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"Getting all logs requires the {nameof(ResourceLoggerService)} instance created by DI.");
        }
    }
}

/// <summary>
/// Represents a log subscriber for a resource.
/// </summary>
/// <param name="Name">The the resource name.</param>
/// <param name="AnySubscribers">Determines if there are any subscribers.</param>
public readonly record struct LogSubscriber(string Name, bool AnySubscribers);
