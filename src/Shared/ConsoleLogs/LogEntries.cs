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
    /// Insert a log entry at the correct position in the list of log entries according to its timestamp.
    /// </summary>
    /// <param name="logLine"></param>
    public void InsertSorted(LogEntry logLine)
    {
        Debug.Assert(logLine.Timestamp == null || logLine.Timestamp.Value.Kind == DateTimeKind.Utc, "Timestamp should always be UTC.");

        InsertSortedCore(logLine);

        // Verify log entry order is correct in debug builds.
        VerifyLogEntryOrder();
    }

    [Conditional("DEBUG")]
    private void VerifyLogEntryOrder()
    {
        DateTimeOffset lastTimestamp = default;
        for (var i = 0; i < _logEntries.Count; i++)
        {
            var entry = _logEntries[i];
            if (entry.Timestamp is { } timestamp && entry.Type is not LogEntryType.Pause)
            {
                if (timestamp < lastTimestamp)
                {
                    throw new InvalidOperationException("Log entries out of order.");
                }
                else
                {
                    lastTimestamp = timestamp;
                }
            }
        }
    }

    private void InsertSortedCore(LogEntry logEntry)
    {
        // If there is no timestamp then add to the end.
        if (logEntry.Timestamp == null)
        {
            InsertAt(_logEntries.Count);
            return;
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
                    InsertAt(missingTimestampIndex.Value + 1);
                    return;
                }

                InsertAt(rowIndex + 1);
                return;
            }
            else
            {
                missingTimestampIndex = null;
            }
        }

        if (_earliestTimestampIndex != null)
        {
            InsertAt(_earliestTimestampIndex.Value);
        }
        else if (missingTimestampIndex != null)
        {
            InsertAt(missingTimestampIndex.Value + 1);
        }
        else
        {
            // New log entry timestamp is smaller than existing entries timestamps.
            // Or maybe there just aren't any other entries yet.
            InsertAt(0);
        }

        void InsertAt(int index)
        {
            if (logEntry.Type is LogEntryType.Pause)
            {
                _pauseViewModels.Add(logEntry.Pause!);
                _logEntries.Insert(index, logEntry);
                return;
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
        }
    }
}
