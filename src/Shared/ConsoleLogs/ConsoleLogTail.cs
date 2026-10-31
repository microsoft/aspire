// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.ConsoleLogs;

/// <summary>
/// Keeps the trailing window of console log lines that contains the last <c>tail</c> application lines.
/// </summary>
/// <remarks>
/// Orchestration lines emitted by the app host (prefixed with <c>[sys]</c>) don't count toward the limit,
/// so a tail of N returns the last N lines of application output. The window starts at the Nth-from-last
/// application line, and <c>[sys]</c> lines after that point are kept so start/stop events stay visible.
/// When there are fewer than N application lines, every line is kept.
/// </remarks>
internal sealed class ConsoleLogTailBuffer<T>
{
    private readonly Queue<T> _items = new();
    private readonly int _tail;
    private readonly Func<T, bool> _isSystemLog;
    private int _applicationLineCount;

    public ConsoleLogTailBuffer(int tail, Func<T, bool> isSystemLog)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tail, 1);
        ArgumentNullException.ThrowIfNull(isSystemLog);

        _tail = tail;
        _isSystemLog = isSystemLog;
    }

    public IEnumerable<T> Items => _applicationLineCount < _tail
        ? _items
        : _items.SkipWhile(_isSystemLog);

    public void Add(T item)
    {
        _items.Enqueue(item);

        if (_isSystemLog(item))
        {
            return;
        }

        _applicationLineCount++;
        if (_applicationLineCount <= _tail)
        {
            return;
        }

        // Drop the oldest application line together with any [sys] lines that precede it.
        // Items trims any [sys] lines left in front of the new oldest application line.
        while (_isSystemLog(_items.Dequeue()))
        {
        }

        _applicationLineCount--;
    }
}

internal static class ConsoleLogTail
{
    private const string SystemLogPrefix = "[sys] ";

    /// <summary>
    /// Returns the trailing window of <paramref name="items"/> that contains the last <paramref name="tail"/>
    /// application lines. See <see cref="ConsoleLogTailBuffer{T}"/>.
    /// </summary>
    public static IEnumerable<T> TakeLast<T>(IEnumerable<T> items, int tail, Func<T, bool> isSystemLog)
    {
        var buffer = new ConsoleLogTailBuffer<T>(tail, isSystemLog);
        foreach (var item in items)
        {
            buffer.Add(item);
        }

        return buffer.Items;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="content"/>, with any timestamp already removed,
    /// is an orchestration line emitted by the app host rather than resource output.
    /// </summary>
    public static bool IsSystemLog(string? content) =>
        content is not null && content.StartsWith(SystemLogPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="content"/>, which may start with a console
    /// timestamp, is an orchestration line emitted by the app host rather than resource output.
    /// </summary>
    public static bool IsSystemLogWithOptionalTimestamp(string? content)
    {
        if (content is null)
        {
            return false;
        }

        return TimestampParser.TryParseConsoleTimestamp(content, out var result)
            ? IsSystemLog(result.Value.ModifiedText)
            : IsSystemLog(content);
    }
}
