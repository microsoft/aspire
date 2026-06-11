// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

internal class BackchannelLoggerProvider : ILoggerProvider
{
    private readonly Queue<BackchannelLogEntry> _replayBuffer = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, Channel<BackchannelLogEntry>> _subscribers = [];
    private int _nextSubscriberId;
    private const int MaxReplayEntries = 1000;

    /// <summary>
    /// Gets a snapshot of buffered log entries and subscribes for new entries.
    /// The returned channel receives all future log entries until disposed.
    /// </summary>
    internal (List<BackchannelLogEntry> Snapshot, int SubscriberId, Channel<BackchannelLogEntry> Channel) Subscribe()
    {
        var channel = Channel.CreateUnbounded<BackchannelLogEntry>();
        lock (_lock)
        {
            var id = _nextSubscriberId++;
            _subscribers[id] = channel;
            // Snapshot under lock so no entries are missed between snapshot and subscribe
            return ([.. _replayBuffer], id, channel);
        }
    }

    internal void Unsubscribe(int subscriberId)
    {
        lock (_lock)
        {
            if (_subscribers.Remove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    internal void WriteEntry(BackchannelLogEntry entry)
    {
        lock (_lock)
        {
            if (_replayBuffer.Count >= MaxReplayEntries)
            {
                _replayBuffer.Dequeue();
            }
            _replayBuffer.Enqueue(entry);

            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryWrite(entry);
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new BackchannelLogger(categoryName, this);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }
}

internal class BackchannelLogger(string categoryName, BackchannelLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return default;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            var entry = new BackchannelLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                CategoryName = categoryName,
                LogLevel = logLevel,
                EventId = eventId,
                Message = formatter(state, exception),
            };

            provider.WriteEntry(entry);
        }
    }
}
