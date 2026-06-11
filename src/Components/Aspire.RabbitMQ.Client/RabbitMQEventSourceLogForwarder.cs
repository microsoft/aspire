// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace Aspire.RabbitMQ.Client;

internal sealed class RabbitMQEventSourceLogForwarder : IDisposable
{
    private static readonly Func<ErrorEventSourceEvent, Exception?, string> s_formatErrorEvent = FormatErrorEvent;

    private readonly ILogger _logger;
    private RabbitMQEventSourceListener? _listener;

    public RabbitMQEventSourceLogForwarder(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger("RabbitMQ.Client");
    }

    /// <summary>
    /// Initiates the log forwarding from the RabbitMQ event sources to a provided <see cref="ILoggerFactory"/>, call <see cref="Dispose"/> to stop forwarding.
    /// </summary>
    public void Start()
    {
        _listener ??= new RabbitMQEventSourceListener(LogEvent, EventLevel.Verbose);
    }

    private void LogEvent(EventWrittenEventArgs eventData)
    {
        var level = MapLevel(eventData.Level);
        var eventId = new EventId(eventData.EventId, eventData.EventName);

        // Special case the Error event so the Exception Details are written correctly
        if (eventData.EventId == 3 &&
            eventData.EventName == "Error" &&
            eventData.PayloadNames?.Count == 2 &&
            eventData.Payload?.Count == 2 &&
            eventData.PayloadNames[0] == "message" &&
            eventData.PayloadNames[1] == "ex")
        {
            _logger.Log(level, eventId, new ErrorEventSourceEvent(eventData), null, s_formatErrorEvent);
        }
        else
        {
            Debug.Assert(
                (eventData.EventId == 1 && eventData.EventName == "Info") ||
                (eventData.EventId == 2 && eventData.EventName == "Warn"));

            _logger.Log(level, eventId, eventData.Payload?[0]?.ToString() ?? "<empty>");
        }
    }

    private static string FormatErrorEvent(ErrorEventSourceEvent eventSourceEvent, Exception? ex) =>
        eventSourceEvent.EventData.Payload?[0]?.ToString() ?? "<empty>";

    public void Dispose() => _listener?.Dispose();

    private static LogLevel MapLevel(EventLevel level) => level switch
    {
        EventLevel.Critical => LogLevel.Critical,
        EventLevel.Error => LogLevel.Error,
        EventLevel.Informational => LogLevel.Information,
        EventLevel.Verbose => LogLevel.Debug,
        EventLevel.Warning => LogLevel.Warning,
        EventLevel.LogAlways => LogLevel.Information,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private readonly struct ErrorEventSourceEvent : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public EventWrittenEventArgs EventData { get; }

        public int Count { get; }

        public ErrorEventSourceEvent(EventWrittenEventArgs eventData)
        {
            EventData = eventData;

            var exData = eventData!.Payload![1] as IDictionary<string, object?>;
            Count = string.IsNullOrEmpty(exData!["InnerException"]?.ToString()) ? 3 : 4;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public KeyValuePair<string, object?> this[int index]
        {
            get
            {
                Debug.Assert(EventData.PayloadNames?.Count == 2 && EventData.Payload?.Count == 2);
                Debug.Assert(EventData.PayloadNames[0] == "message");
                Debug.Assert(EventData.PayloadNames[1] == "ex");

                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

                var exData = EventData.Payload[1] as IDictionary<string, object?>;
                Debug.Assert(exData is not null && exData.Count == 4);

                return index switch
                {
                    0 => new("exception.type", exData["Type"]),
                    1 => new("exception.message", exData["Message"]),
                    2 => new("exception.stacktrace", exData["StackTrace"]),
                    3 => new("exception.innerexception", exData["InnerException"]),
                    _ => throw new UnreachableException()
                };
            }
        }
    }

    /// <summary>
    /// Implementation of <see cref="EventListener"/> that listens to events produced by the RabbitMQ.Client library.
    /// </summary>
    private sealed class RabbitMQEventSourceListener : EventListener
    {
        // This must be initialized here so it is available before the base constructor is called.
        private readonly List<EventSource> _eventSources = [];

        private readonly Action<EventWrittenEventArgs> _log;
        private readonly EventLevel _level;

        public RabbitMQEventSourceListener(Action<EventWrittenEventArgs> log, EventLevel level)
        {
            _log = log;
            _level = level;

            // Enable any event sources that were created before this listener. Safe to do now that _level is set.
            foreach (EventSource eventSource in _eventSources)
            {
                OnEventSourceCreated(eventSource);
            }

            _eventSources.Clear();
        }

        // This is called from the base constructor and so _log and _level may not be initialized yet.
        // https://github.com/dotnet/runtime/blob/15663b5a44d507e0c120f88b6e2e96f977ceac90/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L4153-L4158
        protected sealed override void OnEventSourceCreated(EventSource eventSource)
        {
            // Defensive check for being passed a event source.
            if (eventSource == null)
            {
                return;
            }

            base.OnEventSourceCreated(eventSource);

            // Log will be null during base constructor call. Store registered event source to enable later when class is initialized.
            if (_log == null)
            {
                _eventSources.Add(eventSource);
                return;
            }

            if (eventSource.Name == "rabbitmq-dotnet-client" || eventSource.Name == "rabbitmq-client")
            {
                EnableEvents(eventSource, _level);
            }
        }

        protected sealed override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // Workaround https://github.com/dotnet/corefx/issues/42600
            if (eventData.EventId == -1)
            {
                return;
            }

            // There is a very tight race during the listener creation where EnableEvents was called
            // and the thread producing events not observing the `_log` field assignment
            _log?.Invoke(eventData);
        }
    }
}
