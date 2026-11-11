// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

internal sealed class BuildOutputCapture(ResourceLoggerService loggerService)
{
    internal const string RegisterWriterKey = "Aspire.Hosting.BuildOutputCapture.Register";
    internal const string SubscribeKey = "Aspire.Hosting.BuildOutputCapture.Subscribe";
    private const int MaxBufferedEntries = 10_000;

    private readonly ConcurrentDictionary<string, Writer> _writers = new(StringComparers.ResourceName);
    private readonly ConcurrentDictionary<IResource, Writer> _writersByResource = new(ReferenceEqualityComparer.Instance);

    public IDisposable Register(IResource resource)
    {
        var writer = new Writer(resource);
        if (!_writersByResource.TryAdd(resource, writer))
        {
            throw new InvalidOperationException($"Build output capture is already registered for '{resource.Name}'.");
        }

        return new WriterRegistration(this, writer);
    }

    public IDisposable Register(string resourceName)
    {
        var writer = new Writer(resource: null);
        BindWriter(writer, resourceName);
        return new WriterRegistration(this, writer);
    }

    private void BindWriter(Writer writer, string resourceName)
    {
        lock (writer.Lock)
        {
            ObjectDisposedException.ThrowIf(writer.IsDisposed, writer);
            if (writer.Name is { } existingName)
            {
                if (!StringComparers.ResourceName.Equals(existingName, resourceName))
                {
                    throw new InvalidOperationException($"Build output capture for '{existingName}' cannot be rebound to '{resourceName}'.");
                }
                return;
            }

            if (!_writers.TryAdd(resourceName, writer))
            {
                throw new InvalidOperationException($"Build output capture is already registered for '{resourceName}'.");
            }

            try
            {
                // A subscriber keeps the DCP follow stream active so a later project Start can
                // replay this attempt even when a dashboard is already watching the build resource.
                writer.LogSubscription = loggerService.Subscribe(resourceName, _ => { });
                writer.Name = resourceName;
            }
            catch
            {
                _writers.TryRemove(new(resourceName, writer));
                throw;
            }
        }
    }

    public IDisposable Subscribe(string resourceName, IEnumerable<string> targetIds, bool includeCompleted)
    {
        if (!_writers.TryGetValue(resourceName, out var writer))
        {
            throw new InvalidOperationException($"Build output capture is not registered for '{resourceName}'.");
        }

        var targets = targetIds.Distinct(StringComparers.ResourceName).ToArray();
        lock (writer.Lock)
        {
            ObjectDisposedException.ThrowIf(writer.IsDisposed, writer);
            foreach (var targetId in targets)
            {
                if (!writer.Targets.TryGetValue(targetId, out var target))
                {
                    target = new Target(loggerService.GetLogger(targetId));
                    writer.Targets.Add(targetId, target);
                }

                if (target.Count++ == 0 && writer.Attempt is { } attempt)
                {
                    if (attempt.IsActive || includeCompleted)
                    {
                        foreach (var (sequence, entry) in attempt.Buffer)
                        {
                            if (sequence > target.LastSequence)
                            {
                                Forward(target, entry, sequence);
                            }
                        }
                    }
                    else
                    {
                        target.LastSequence = attempt.LastSequence;
                    }
                }
            }
        }

        return new TargetSubscription(writer, targets);
    }

    public IDisposable Subscribe(IResource writer, BeforeResourceStartedEvent @event, bool includeCompleted)
    {
        if (!_writersByResource.TryGetValue(writer, out var registration))
        {
            throw new InvalidOperationException($"Build output capture is not registered for '{writer.Name}'.");
        }

        // DCP instance names are assigned after the build plan is materialized.
        var writerName = writer.GetResolvedResourceNames()[0];
        BindWriter(registration, writerName);
        var targetIds = @event.ResourceInstanceId is { } resourceId
            ? [resourceId]
            : @event.Resource.GetResolvedResourceNames();
        return Subscribe(writerName, targetIds, includeCompleted);
    }

    public void BeginAttempt(IResource resource, string? resourceName)
    {
        if (_writersByResource.TryGetValue(resource, out var writer))
        {
            resourceName ??= resource.GetResolvedResourceNames()[0];
            BindWriter(writer, resourceName);
            BeginAttempt(resourceName);
        }
    }

    public void BeginAttempt(string resourceName)
    {
        if (_writers.TryGetValue(resourceName, out var writer))
        {
            lock (writer.Lock)
            {
                if (writer.IsDisposed)
                {
                    return;
                }

                writer.Attempt = new Attempt(writer, writer.LastObservedUid, ++writer.AttemptNumber);
                foreach (var target in writer.Targets.Values)
                {
                    target.LastSequence = 0;
                }
            }
        }
    }

    public void Observe(string resourceName, string? resourceUid)
    {
        if (_writers.TryGetValue(resourceName, out var writer))
        {
            lock (writer.Lock)
            {
                if (writer.Attempt is { IsBound: false } attempt &&
                    (attempt.Number == 1 || !string.Equals(attempt.PreviousUid, resourceUid, StringComparison.Ordinal)))
                {
                    attempt.ResourceUid = resourceUid;
                    attempt.IsBound = true;
                }

                writer.LastObservedUid = resourceUid;
            }
        }
    }

    public Attempt? GetAttempt(string resourceName, string? resourceUid)
    {
        if (_writers.TryGetValue(resourceName, out var writer))
        {
            lock (writer.Lock)
            {
                return writer.Attempt is { IsBound: true } attempt &&
                    string.Equals(attempt.ResourceUid, resourceUid, StringComparison.Ordinal)
                    ? attempt
                    : null;
            }
        }

        return null;
    }

    public static void AddFollow(Attempt? attempt, IReadOnlyList<LogEntry> entries)
    {
        if (attempt is null)
        {
            return;
        }

        var writer = attempt.Writer;
        lock (writer.Lock)
        {
            if (!ReferenceEquals(writer.Attempt, attempt))
            {
                return;
            }

            var pending = attempt.PendingFollow;
            foreach (var entry in entries)
            {
                var key = LogEntryKey.Create(entry);
                if (pending is not null && Consume(pending.Counts, key))
                {
                    pending.RemainingCount--;
                    continue;
                }

                AddEntry(writer, attempt, entry);
            }

            // The snapshot and follow streams overlap only around a terminal flush. A batch
            // without timestamps cannot establish that boundary, so retain it for one batch only.
            if (pending is not null &&
                (pending.LatestTimestamp is null ||
                 pending.RemainingCount == 0 ||
                 entries.Any(entry => entry.Timestamp is null || entry.Timestamp > pending.LatestTimestamp)))
            {
                attempt.PendingFollow = null;
            }
        }
    }

    public void AddInMemory(string resourceName, LogEntry entry)
    {
        if (_writers.TryGetValue(resourceName, out var writer))
        {
            lock (writer.Lock)
            {
                if (writer.Attempt is { IsActive: true } attempt)
                {
                    AddEntry(writer, attempt, entry);
                }
            }
        }
    }

    public static void AddTerminal(Attempt? attempt, IReadOnlyList<LogEntry> entries)
    {
        if (attempt is null)
        {
            return;
        }

        var writer = attempt.Writer;
        lock (writer.Lock)
        {
            if (!ReferenceEquals(writer.Attempt, attempt))
            {
                return;
            }

            // A terminal snapshot can overlap the follow stream in either direction. Count
            // occurrences rather than using a set so repeated identical build lines survive.
            var observed = new Dictionary<LogEntryKey, int>();
            foreach (var (_, entry) in attempt.Buffer)
            {
                Increment(observed, LogEntryKey.Create(entry));
            }

            var pending = new PendingFollowDeduplication();
            // Match the bounded replay window rather than retaining every line of a long build.
            foreach (var entry in entries.TakeLast(MaxBufferedEntries))
            {
                var key = LogEntryKey.Create(entry);
                Increment(pending.Counts, key);
                pending.RemainingCount++;
                if (entry.Timestamp is { } timestamp &&
                    (pending.LatestTimestamp is null || timestamp > pending.LatestTimestamp))
                {
                    pending.LatestTimestamp = timestamp;
                }

                if (!Consume(observed, key))
                {
                    AddEntry(writer, attempt, entry);
                }
            }

            attempt.PendingFollow = pending.RemainingCount > 0 ? pending : null;
        }
    }

    public static void Complete(Attempt? attempt)
    {
        if (attempt is null)
        {
            return;
        }

        lock (attempt.Writer.Lock)
        {
            if (ReferenceEquals(attempt.Writer.Attempt, attempt))
            {
                attempt.IsActive = false;
            }
        }
    }

    public void CompleteCurrent(string resourceName)
    {
        if (_writers.TryGetValue(resourceName, out var writer))
        {
            lock (writer.Lock)
            {
                if (writer.Attempt is { } attempt)
                {
                    attempt.IsActive = false;
                }
            }
        }
    }

    private static void AddEntry(Writer writer, Attempt attempt, LogEntry entry)
    {
        var sequence = ++attempt.LastSequence;
        attempt.Buffer.Enqueue((sequence, entry));
        if (attempt.Buffer.Count > MaxBufferedEntries)
        {
            attempt.Buffer.Dequeue();
        }

        foreach (var target in writer.Targets.Values)
        {
            if (target.Count > 0)
            {
                Forward(target, entry, sequence);
            }
        }
    }

    private static void Forward(Target target, LogEntry entry, long sequence)
    {
        if (entry.Type == LogEntryType.Error)
        {
            target.Logger.LogWarning(CommandsConfigurationExtensions.BuildLogPrefix + "{Content}", entry.Content);
        }
        else
        {
            target.Logger.LogInformation(CommandsConfigurationExtensions.BuildLogPrefix + "{Content}", entry.Content);
        }

        target.LastSequence = sequence;
    }

    private static void Increment(Dictionary<LogEntryKey, int> counts, LogEntryKey key)
    {
        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

    private static bool Consume(Dictionary<LogEntryKey, int> counts, LogEntryKey key)
    {
        if (counts.TryGetValue(key, out var count) && count > 0)
        {
            counts[key] = count - 1;
            return true;
        }

        return false;
    }

    internal sealed class Attempt(Writer writer, string? previousUid, long number)
    {
        public Writer Writer { get; } = writer;
        public string? PreviousUid { get; } = previousUid;
        public long Number { get; } = number;
        public string? ResourceUid { get; set; }
        public bool IsBound { get; set; }
        public bool IsActive { get; set; } = true;
        public long LastSequence { get; set; }
        public Queue<(long Sequence, LogEntry Entry)> Buffer { get; } = new();
        public PendingFollowDeduplication? PendingFollow { get; set; }
    }

    internal sealed class PendingFollowDeduplication
    {
        public Dictionary<LogEntryKey, int> Counts { get; } = [];
        public DateTime? LatestTimestamp { get; set; }
        public int RemainingCount { get; set; }
    }

    internal sealed class Writer(IResource? resource)
    {
        public IResource? Resource { get; } = resource;
        public string? Name { get; set; }
        public Lock Lock { get; } = new();
        public bool IsDisposed { get; set; }
        public string? LastObservedUid { get; set; }
        public long AttemptNumber { get; set; }
        public Attempt? Attempt { get; set; }
        public Dictionary<string, Target> Targets { get; } = new(StringComparers.ResourceName);
        public IDisposable? LogSubscription { get; set; }
    }

    internal sealed class Target(ILogger logger)
    {
        public ILogger Logger { get; } = logger;
        public int Count { get; set; }
        public long LastSequence { get; set; }
    }

    private sealed class WriterRegistration(BuildOutputCapture owner, Writer writer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (writer.Lock)
                {
                    writer.IsDisposed = true;
                    writer.Attempt = null;
                    if (writer.Resource is { } resource)
                    {
                        owner._writersByResource.TryRemove(new(resource, writer));
                    }
                    if (writer.Name is { } name && owner._writers.TryRemove(new(name, writer)))
                    {
                        writer.LogSubscription?.Dispose();
                    }
                }
            }
        }
    }

    private sealed class TargetSubscription(Writer writer, string[] targets) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (writer.Lock)
                {
                    foreach (var target in targets)
                    {
                        writer.Targets[target].Count--;
                    }
                }
            }
        }
    }
}
