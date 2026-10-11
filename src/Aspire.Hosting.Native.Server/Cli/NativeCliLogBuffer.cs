// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.Native.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Cli;

/// <summary>Retains bounded, classified AppHost diagnostic logs independently of CLI connections.</summary>
internal sealed class NativeCliLogBuffer(int capacity) : IObserver<OperationEvent>
{
    private readonly Lock _gate = new();
    private readonly Queue<NativeCliLogEntry> _entries = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private readonly Guid _generation = Guid.NewGuid();

    public void OnNext(OperationEvent value)
    {
        lock (_gate)
        {
            var sequence = checked(++_sequence);
            _entries.Enqueue(new NativeCliLogEntry(sequence, _generation, new EventId(0, value.Operation),
                value.ErrorType is null ? LogLevel.Debug : LogLevel.Error,
                $"{value.Operation}: {value.Outcome}" + (value.ErrorType is null ? "" : $" ({value.ErrorType})"),
                value.Timestamp, NativeDiagnostics.SourceName));
            if (_entries.Count > capacity)
            {
                _entries.Dequeue();
            }
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    public async IAsyncEnumerable<NativeCliLogEntry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long sequence = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeCliLogEntry[] entries;
            Task changed;
            lock (_gate)
            {
                entries = _entries.Where(entry => entry.SequenceNumber > sequence).ToArray();
                changed = _changed.Task;
            }
            foreach (var entry in entries)
            {
                sequence = entry.SequenceNumber;
                yield return entry;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) => throw new InvalidOperationException("Native diagnostic logging failed.", error);
}
