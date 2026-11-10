// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Backchannel;

internal class BackchannelLoggerProvider : ILoggerProvider
{
    private readonly Queue<BackchannelLogEntry> _replayBuffer = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, Channel<BackchannelLogEntry>> _subscribers = [];
    private readonly Guid _generationId = Guid.NewGuid();
    private int _nextSubscriberId;
    private long _nextSequenceNumber;
    private readonly IOptionsMonitor<ConsoleLoggerOptions>? _consoleLoggerOptions;
    private readonly IOptionsMonitor<SimpleConsoleFormatterOptions>? _simpleConsoleFormatterOptions;
    private const int MaxReplayEntries = 1000;

    /// <summary>
    /// Creates a provider that does not describe a console logger, so its entries carry no
    /// <see cref="BackchannelLogEntry.ConsoleFormat"/>.
    /// </summary>
    public BackchannelLoggerProvider()
    {
    }

    public BackchannelLoggerProvider(
        IOptionsMonitor<ConsoleLoggerOptions> consoleLoggerOptions,
        IOptionsMonitor<SimpleConsoleFormatterOptions> simpleConsoleFormatterOptions)
    {
        _consoleLoggerOptions = consoleLoggerOptions;
        _simpleConsoleFormatterOptions = simpleConsoleFormatterOptions;
    }

    internal BackchannelConsoleFormat? GetConsoleFormat()
    {
        // Read the options per record so a reloaded configuration is reported with the
        // records the console logger rendered using it.
        return _consoleLoggerOptions is null || _simpleConsoleFormatterOptions is null
            ? null
            : GetConsoleFormat(_consoleLoggerOptions.CurrentValue, _simpleConsoleFormatterOptions.CurrentValue);
    }

    internal static BackchannelConsoleFormat? GetConsoleFormat(
        ConsoleLoggerOptions consoleLoggerOptions,
        SimpleConsoleFormatterOptions simpleConsoleFormatterOptions)
    {
        // Mirrors how ConsoleLoggerProvider.ReloadLoggerOptions picks the formatter and its options:
        // https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Logging.Console/src/ConsoleLoggerProvider.cs
        // - No FormatterName: the obsolete ConsoleLoggerOptions.Format picks the formatter, and the
        //   simple formatter is configured from the obsolete ConsoleLoggerOptions properties.
        // - FormatterName "simple" (matched case-insensitively): SimpleConsoleFormatterOptions applies.
        // Anything else (json, systemd, custom) is not the simple formatter's shape, so report
        // nothing and let the client keep its fallback parser.
#pragma warning disable CS0618 // Type or member is obsolete
        return consoleLoggerOptions.FormatterName switch
        {
            null when consoleLoggerOptions.Format is ConsoleLoggerFormat.Default => new()
            {
                TimestampFormat = consoleLoggerOptions.TimestampFormat,
                IncludeScopes = consoleLoggerOptions.IncludeScopes,
            },
            string name when string.Equals(name, ConsoleFormatterNames.Simple, StringComparison.OrdinalIgnoreCase) => new()
            {
                TimestampFormat = simpleConsoleFormatterOptions.TimestampFormat,
                SingleLine = simpleConsoleFormatterOptions.SingleLine,
                IncludeScopes = simpleConsoleFormatterOptions.IncludeScopes,
            },
            _ => null
        };
#pragma warning restore CS0618
    }

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
            entry.GenerationId = _generationId;
            entry.SequenceNumber = ++_nextSequenceNumber;

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
                Exception = exception?.ToString(),
                ConsoleFormat = provider.GetConsoleFormat(),
            };

            provider.WriteEntry(entry);
        }
    }
}
