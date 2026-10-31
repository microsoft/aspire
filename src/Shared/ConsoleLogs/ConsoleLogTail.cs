// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.ConsoleLogs;

/// <summary>
/// Keeps the trailing window of console log lines that contains the last <c>tail</c> application lines.
/// </summary>
/// <remarks>
/// <para>
/// Orchestration lines emitted by the app host (prefixed with <c>[sys]</c>) don't count toward the limit,
/// so a tail of N returns the last N lines of application output. The window starts at the Nth-from-last
/// application line, and <c>[sys]</c> lines after that point are kept so start/stop events stay visible.
/// When there are fewer than N application lines, every line is kept.
/// </para>
/// <para>
/// Lines are ordered by their timestamp when one is available, then by arrival order. On the app host,
/// application output and <c>[sys]</c> lines are read from separate streams, so a <c>[sys]</c> line can
/// arrive before older application output. Ordering by timestamp before trimming keeps such a line when it
/// belongs inside the window. Lines without a timestamp sort right after the line that arrived before them.
/// </para>
/// </remarks>
internal sealed class ConsoleLogTailBuffer<T>
{
    // [sys] lines older than every kept application line can never be returned again, because the earliest
    // kept application line only moves forward. They're pruned when that boundary advances, once the list has
    // grown past a threshold that doubles with the lines that survive, so pruning is amortized O(1) per line.
    private const int MinimumSystemLinePruneThreshold = 1024;

    private readonly int _tail;
    private readonly Func<T, bool> _isSystemLog;
    private readonly Func<T, DateTimeOffset?>? _getTimestamp;
    private readonly PriorityQueue<Entry, Entry> _applicationLines;
    private readonly List<Entry> _systemLines = [];
    private int _systemLinePruneThreshold = MinimumSystemLinePruneThreshold;
    private DateTimeOffset _lastTimestamp = DateTimeOffset.MinValue;
    private long _sequence;

    public ConsoleLogTailBuffer(int tail, Func<T, bool> isSystemLog, Func<T, DateTimeOffset?>? getTimestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tail, 1);
        ArgumentNullException.ThrowIfNull(isSystemLog);

        _tail = tail;
        _isSystemLog = isSystemLog;
        _getTimestamp = getTimestamp;
        // Don't size the queue from the tail: it can be as large as int.MaxValue while the log history is small.
        _applicationLines = new PriorityQueue<Entry, Entry>(EntryComparer.Instance);
    }

    public IEnumerable<T> Items
    {
        get
        {
            var entries = new List<Entry>(_applicationLines.Count + _systemLines.Count);
            foreach (var (entry, _) in _applicationLines.UnorderedItems)
            {
                entries.Add(entry);
            }

            if (_applicationLines.Count < _tail)
            {
                entries.AddRange(_systemLines);
            }
            else
            {
                var earliestKept = _applicationLines.Peek();
                entries.AddRange(_systemLines.Where(e => EntryComparer.Instance.Compare(e, earliestKept) > 0));
            }

            entries.Sort(EntryComparer.Instance);
            return entries.ConvertAll(e => e.Item);
        }
    }

    public void Add(T item)
    {
        if (_getTimestamp?.Invoke(item) is { } timestamp)
        {
            _lastTimestamp = timestamp;
        }

        var entry = new Entry(item, _lastTimestamp, _sequence++);

        if (_isSystemLog(item))
        {
            // A full window never moves back, so a [sys] line older than it can be discarded right away.
            if (_applicationLines.Count < _tail || EntryComparer.Instance.Compare(entry, _applicationLines.Peek()) > 0)
            {
                _systemLines.Add(entry);
            }

            return;
        }

        _applicationLines.Enqueue(entry, entry);
        if (_applicationLines.Count > _tail)
        {
            _applicationLines.Dequeue();
            PruneSystemLines();
        }
    }

    private void PruneSystemLines()
    {
        if (_systemLines.Count <= _systemLinePruneThreshold)
        {
            return;
        }

        var earliestKept = _applicationLines.Peek();
        _systemLines.RemoveAll(e => EntryComparer.Instance.Compare(e, earliestKept) < 0);
        _systemLinePruneThreshold = Math.Max(MinimumSystemLinePruneThreshold, _systemLines.Count * 2);
    }

    internal int RetainedSystemLineCount => _systemLines.Count;

    private readonly record struct Entry(T Item, DateTimeOffset Timestamp, long Sequence);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();

        public int Compare(Entry x, Entry y)
        {
            var result = x.Timestamp.CompareTo(y.Timestamp);
            return result != 0 ? result : x.Sequence.CompareTo(y.Sequence);
        }
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

    /// <summary>
    /// Returns the timestamp at the start of <paramref name="content"/>, or <see langword="null"/> when there isn't one.
    /// </summary>
    public static DateTimeOffset? GetTimestamp(string? content) =>
        content is not null && TimestampParser.TryParseConsoleTimestamp(content, out var result)
            ? result.Value.Timestamp
            : null;
}
