// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aspire.Hosting.Native.Diagnostics;

/// <summary>Emits local diagnostic activities, structured events, and bounded operation metrics.</summary>
internal static class NativeDiagnostics
{
    public const string SourceName = "Aspire.Hosting.Native.Core";
    private static readonly ActivitySource s_activities = new(SourceName);
    private static readonly OperationEventSource s_events = new();
    private static readonly Meter s_meter = new(SourceName);
    private static readonly Counter<long> s_operations = s_meter.CreateCounter<long>("aspire.native.operations", "{operation}");
    private static readonly Histogram<double> s_duration = s_meter.CreateHistogram<double>("aspire.native.operation.duration", "s");

    public static IObservable<OperationEvent> Events => s_events;

    public static T Execute<T>(string name, Guid? sessionId, Guid? generationId, Func<Operation, T> action)
    {
        using var operation = new Operation(name, sessionId, generationId);
        try
        {
            var result = action(operation);
            operation.Succeeded = true;

            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Exception messages can contain user input. Keep the original
            // exception for the caller, but emit only its classification.
            operation.ErrorType = exception.GetType().FullName;
            throw;
        }
    }

    public static void Execute(string name, Guid? sessionId, Guid? generationId, Action<Operation> action) =>
        Execute(name, sessionId, generationId, operation =>
        {
            action(operation);
            return true;
        });

    /// <summary>Correlates one operation without recording arguments, configuration, or exception messages.</summary>
    internal sealed class Operation : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string _name;
        private readonly Guid _operationId;
        private readonly Guid? _sessionId;
        private Guid? _generationId;
        private Guid? _resourceId;

        internal Operation(string name, Guid? sessionId, Guid? generationId)
        {
            _name = name;
            _sessionId = sessionId;
            _generationId = generationId;
            _operationId = Guid.NewGuid();
            _activity = s_activities.StartActivity(name, ActivityKind.Internal);
            _activity?.SetTag("aspire.session.id", sessionId);
            _activity?.SetTag("aspire.generation.id", generationId);
            Publish("started", 0);
        }

        internal bool Succeeded { get; set; }
        internal string? ErrorType { get; set; }

        internal void SetGeneration(Guid generationId)
        {
            _generationId = generationId;
            _activity?.SetTag("aspire.generation.id", generationId);
        }

        internal void SetResource(Guid resourceId)
        {
            _resourceId = resourceId;
            _activity?.SetTag("aspire.resource.id", resourceId);
        }

        public void Dispose()
        {
            var outcome = Succeeded ? "ok" : "error";
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            _activity?.SetStatus(Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            if (ErrorType is not null)
            {
                _activity?.SetTag("error.type", ErrorType);
            }

            // Resource, session, generation, and trace identities belong in
            // events/spans, never metric dimensions with unbounded cardinality.
            var tags = new TagList { { "operation", _name }, { "outcome", outcome } };
            s_operations.Add(1, tags);
            s_duration.Record(elapsed, tags);
            try
            {
                Publish(outcome, elapsed);
            }
            finally
            {
                _activity?.Dispose();
            }
        }

        private void Publish(string outcome, double elapsed)
        {
            if (!s_events.HasObservers)
            {
                return;
            }
            var activity = _activity ?? Activity.Current;
            s_events.Publish(new OperationEvent(
                _operationId, _name, outcome, _sessionId, _generationId, _resourceId,
                activity?.TraceId.ToString(), activity?.SpanId.ToString(), DateTimeOffset.UtcNow, elapsed, ErrorType));
        }
    }
}

/// <summary>Describes a local diagnostic operation independently of trace sampling.</summary>
internal sealed record OperationEvent(
    Guid OperationId, string Operation, string Outcome, Guid? SessionId, Guid? GenerationId,
    Guid? ResourceId, string? TraceId, string? SpanId, DateTimeOffset Timestamp, double DurationSeconds, string? ErrorType);
