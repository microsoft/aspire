// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Shared.ConsoleLogs;

// Type is shared by dashboard and hosting.
// It needs to be public in dashboard so it can be bound to a parameter.
// It needs to be internal in hosting because we don't want to expose it as public API.
[DebuggerDisplay("Count = {EntriesCount}")]
#if ASPIRE_DASHBOARD
public sealed class LogEntries(int maximumEntryCount)
#else
internal sealed class LogEntries(int maximumEntryCount)
#endif
{
    private readonly List<LogPauseViewModel> _pauseViewModels = [];
    private readonly CircularBuffer<LogEntry> _logEntries = new(maximumEntryCount);

    private int? _earliestTimestampIndex;

    // Keep track of the base line number to ensure that we can calculate the line number of each log entry.
    // This becomes important when the total number of log entries exceeds the limit and is truncated.
    public int? BaseLineNumber { get; set; }

    public IList<LogEntry> GetEntries() => _logEntries;

    public int EntriesCount => _logEntries.Count;

    public void Clear(bool keepActivePauseEntries)
    {
        if (keepActivePauseEntries)
        {
            // Don't remove pause VMs or their entries that are still active.
            _pauseViewModels.RemoveAll(pause => pause.EndTime is not null);
            foreach (var pauseVM in _pauseViewModels)
            {
                // Reset filtered count to zero because all the entries have been cleared.
                pauseVM.FilteredCount = 0;
            }

            var pauseEntries = _logEntries.Where(e => e.Type == LogEntryType.Pause && _pauseViewModels.Contains(e.Pause!)).ToList();
            _logEntries.Clear();
            foreach (var pauseEntry in pauseEntries)
            {
                _logEntries.Add(pauseEntry);
            }
        }
        else
        {
            _pauseViewModels.Clear();
            _logEntries.Clear();
        }

        BaseLineNumber = null;
        _earliestTimestampIndex = null;
    }

    public bool ProcessPauseFilters(LogEntry logEntry)
    {
        if (logEntry.Timestamp is null)
        {
            return false;
        }

        foreach (var pauseVM in _pauseViewModels)
        {
            if (pauseVM.ResourcePrefix == logEntry.ResourcePrefix && pauseVM.Contains(logEntry.Timestamp.Value))
            {
                pauseVM.FilteredCount += 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts log entries at their positions according to their timestamps, like calling <see cref="InsertSorted"/>
    /// for each entry in timestamp order.
    /// </summary>
    /// <remarks>
    /// Inserting an entry before the newest entries moves and renumbers every entry after it. Logs of several resources
    /// arrive one resource at a time, so most of a resource's history lands before another resource's entries, and
    /// inserting entries one by one costs time proportional to the entry count for each entry. When every entry has a
    /// timestamp and no pause is displayed, the batch is merged with the existing entries in one pass instead. Entries
    /// are then numbered consecutively by their position in the merged history.
    /// </remarks>
    public void InsertSortedRange(IReadOnlyList<LogEntry> logEntries)
    {
        if (!CanMerge(logEntries))
        {
            foreach (var logEntry in logEntries)
            {
                InsertSorted(logEntry);
            }

            return;
        }

        // A stable sort keeps entries with the same timestamp in their batch order, like inserting them one by one.
        var batch = logEntries.OrderBy(e => e.Timestamp!.Value).ToList();
        var existing = _logEntries.ToList();

        // Merge the entries. An entry is inserted after existing entries with the same timestamp.
        var merged = new List<LogEntry>(existing.Count + batch.Count);
        var existingIndex = 0;
        var batchIndex = 0;
        while (existingIndex < existing.Count || batchIndex < batch.Count)
        {
            merged.Add(batchIndex == batch.Count ||
                (existingIndex < existing.Count && existing[existingIndex].Timestamp!.Value <= batch[batchIndex].Timestamp!.Value)
                ? existing[existingIndex++]
                : batch[batchIndex++]);
        }

        // Keep the newest entries, like a full buffer does, and number the merged entries consecutively from the first
        // existing entry's line number, so a kept entry's number counts the entries before it, including dropped ones.
        var firstLineNumber = existing[0].LineNumber;
        var skipped = Math.Max(0, merged.Count - _logEntries.Capacity);
        _logEntries.Clear();
        for (var i = skipped; i < merged.Count; i++)
        {
            merged[i].LineNumber = firstLineNumber + i;
            _logEntries.Add(merged[i]);
        }

        // Every entry has a timestamp, so the first one is the earliest timestamped entry.
        if (_logEntries.Count > 0)
        {
            _earliestTimestampIndex = 0;
        }
    }

    private bool CanMerge(IReadOnlyList<LogEntry> logEntries)
    {
        // Small batches and batches that only append are cheaper to insert one by one.
        if (logEntries.Count < 16 || _pauseViewModels.Count > 0 || BaseLineNumber is null ||
            _earliestTimestampIndex is not (null or 0))
        {
            return false;
        }

        DateTime? earliest = null;
        foreach (var logEntry in logEntries)
        {
            if (logEntry is not { Timestamp: { } timestamp, Type: not LogEntryType.Pause })
            {
                return false;
            }

            earliest = earliest is null || timestamp < earliest ? timestamp : earliest;
        }

        if (_logEntries.Count == 0 || earliest >= _logEntries[_logEntries.Count - 1].Timestamp)
        {
            return false;
        }

        foreach (var existingEntry in _logEntries)
        {
            if (existingEntry is not { Timestamp: not null, Type: not LogEntryType.Pause })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Insert a log entry at the correct position in the list of log entries according to its timestamp.
    /// </summary>
    /// <param name="logLine"></param>
    public void InsertSorted(LogEntry logLine)
    {
        Debug.Assert(logLine.Timestamp == null || logLine.Timestamp.Value.Kind == DateTimeKind.Utc, "Timestamp should always be UTC.");

        var index = InsertSortedCore(logLine);

        // Verify log entry order is correct in debug builds. Inserting into a full buffer also evicts its oldest entry,
        // which moves the inserted entry back by one, so check both positions.
        VerifyLogEntryOrder(index - 1);
        VerifyLogEntryOrder(index);
    }

    // Entries were in order before the insert, so only the inserted entry can be out of order with its neighbors.
    // Checking just those keeps Debug builds responsive: scanning every entry after each insert made a full console
    // log buffer cost a whole core under a steady stream of logs.
    [Conditional("DEBUG")]
    private void VerifyLogEntryOrder(int index)
    {
        if (index < 0 || index >= _logEntries.Count || _logEntries[index] is not { Timestamp: { } timestamp, Type: not LogEntryType.Pause })
        {
            return;
        }

        if (FindTimestamp(index - 1, -1) is { } previous && timestamp < previous ||
            FindTimestamp(index + 1, 1) is { } next && timestamp > next)
        {
            throw new InvalidOperationException("Log entries out of order.");
        }

        DateTime? FindTimestamp(int start, int step)
        {
            for (var i = start; i >= 0 && i < _logEntries.Count; i += step)
            {
                if (_logEntries[i] is { Timestamp: { } value, Type: not LogEntryType.Pause })
                {
                    return value;
                }
            }

            return null;
        }
    }

    private int InsertSortedCore(LogEntry logEntry)
    {
        // If there is no timestamp then add to the end.
        if (logEntry.Timestamp == null)
        {
            return InsertAt(_logEntries.Count);
        }

        int? missingTimestampIndex = null;
        for (var rowIndex = _logEntries.Count - 1; rowIndex >= 0; rowIndex--)
        {
            var current = _logEntries[rowIndex];

            // If the current entry has no timestamp then we can't match against it.
            // Keep a track of the first entry with no timestamp, as we want to insert
            // ahead of it if we can't find a more exact place to insert the entry.
            if (current.Timestamp == null)
            {
                if (missingTimestampIndex == null)
                {
                    missingTimestampIndex = rowIndex;
                }

                continue;
            }

            // Add log entry if it is later than current entry.
            if (logEntry.Timestamp.Value >= current.Timestamp.Value)
            {
                // If there were lines with no timestamp before current entry
                // then insert after the lines with no timestamp.
                if (missingTimestampIndex != null)
                {
                    return InsertAt(missingTimestampIndex.Value + 1);
                }

                return InsertAt(rowIndex + 1);
            }
            else
            {
                missingTimestampIndex = null;
            }
        }

        if (_earliestTimestampIndex != null)
        {
            return InsertAt(_earliestTimestampIndex.Value);
        }
        else if (missingTimestampIndex != null)
        {
            return InsertAt(missingTimestampIndex.Value + 1);
        }
        else
        {
            // New log entry timestamp is smaller than existing entries timestamps.
            // Or maybe there just aren't any other entries yet.
            return InsertAt(0);
        }

        int InsertAt(int index)
        {
            if (logEntry.Type is LogEntryType.Pause)
            {
                _pauseViewModels.Add(logEntry.Pause!);
                _logEntries.Insert(index, logEntry);
                return index;
            }

            // Set the line number of the log entry.
            if (index == 0)
            {
                Debug.Assert(BaseLineNumber != null, "Should be set before this method is run.");
                logEntry.LineNumber = BaseLineNumber.Value;
            }
            else if (_logEntries[index - 1].Pause is { } pause)
            {
                Debug.Assert(pause.EndTime is not null, "Pause should have ended before trying to insert another log.");

                int? previousLineNumber = null;
                for (var i = index - 1; i >= 0; i--)
                {
                    if (_logEntries[i] is { Type: not LogEntryType.Pause } entry)
                    {
                        previousLineNumber = entry.LineNumber;
                        break;
                    }
                }

                if (previousLineNumber is not null)
                {
                    logEntry.LineNumber = previousLineNumber.Value + pause.FilteredCount + 1;
                }
                else
                {
                    Debug.Assert(BaseLineNumber is not null);
                    logEntry.LineNumber = BaseLineNumber.Value + pause.FilteredCount;
                }
            }
            else
            {
                logEntry.LineNumber = _logEntries[index - 1].LineNumber + 1;
            }

            if (_earliestTimestampIndex == null && logEntry.Timestamp != null)
            {
                _earliestTimestampIndex = index;
            }

            // Insert the entry.
            _logEntries.Insert(index, logEntry);

            // If a log entry isn't inserted at the end then update the line numbers of all subsequent entries.
            for (var i = index + 1; i < _logEntries.Count; i++)
            {
                _logEntries[i].LineNumber++;
            }

            return index;
        }
    }
}
