// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// In-memory logger that records every log call so tests can assert on the
/// structured messages a component emits.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = new();

    /// <summary>
    /// Gets a snapshot of the log entries recorded so far.
    /// </summary>
    /// <remarks>
    /// Loggers are written to from whatever thread produced the log call, and some components
    /// under test log from concurrent work (for example fan-out connection attempts). A snapshot
    /// is returned so callers can enumerate without racing a concurrent write.
    /// </remarks>
    public List<(LogLevel Level, EventId EventId, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
