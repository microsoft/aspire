// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;

namespace OpenTelemetry.Instrumentation.ConfluentKafka;

[EventSource(Name = "OpenTelemetry-Instrumentation-ConfluentKafka")]
internal sealed class ConfluentKafkaInstrumentationEventSource : EventSource
{
    public static ConfluentKafkaInstrumentationEventSource Log = new();

    private const int EventIdFailedToFetchClusterId = 1;

    [NonEvent]
    public void FailedToFetchClusterId(Exception ex)
    {
        if (this.IsEnabled(EventLevel.Warning, EventKeywords.All))
        {
            this.FailedToFetchClusterId(ex.ToString());
        }
    }

    [Event(EventIdFailedToFetchClusterId, Message = "Failed to fetch Kafka cluster id. Exception: '{0}'", Level = EventLevel.Warning)]
    public void FailedToFetchClusterId(string exception)
    {
        this.WriteEvent(EventIdFailedToFetchClusterId, exception);
    }
}
