// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Tests;

/// <summary>
/// Captures every log entry the publisher writes, so tests can assert on diagnostics the publisher
/// emits instead of failing (warnings about omitted variables, replaced credentials, and databases
/// the recipe will not create).
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));

    /// <summary>
    /// Returns the messages logged at <paramref name="level"/> that contain every fragment in
    /// <paramref name="fragments"/>. Matching on fragments rather than the whole message keeps the
    /// assertion readable without pinning wording that is free to change.
    /// </summary>
    public IReadOnlyList<string> Matching(LogLevel level, params string[] fragments) =>
        _entries
            .Where(e => e.Level == level && fragments.All(f => e.Message.Contains(f, StringComparison.Ordinal)))
            .Select(e => e.Message)
            .ToList();
}
