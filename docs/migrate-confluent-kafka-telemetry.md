# Kafka telemetry migration

## Breaking-change release note

`Aspire.Confluent.Kafka` updates its vendored OpenTelemetry Kafka instrumentation from `0.1.0-alpha.2` to `0.3.0-alpha.4`, following OpenTelemetry messaging semantic conventions v1.44.0. **This breaks the default telemetry contract:** metric names, attribute names and types, and span names and kinds change. Update Kafka dashboards, alerts, recording rules, and queries using the mappings below before upgrading applications.

Public .NET registration APIs, settings, and connection-string handling are unchanged. The upstream instrumentation's alpha status does not make the stable Aspire integration's telemetry contract experimental. This change is intended for a major Aspire release, not a compatible servicing update.

## Metrics

These changes apply to the `OpenTelemetry.Instrumentation.ConfluentKafka` meter:

| Previous metric | Replacement | Selection |
| --- | --- | --- |
| `messaging.publish.duration` | `messaging.client.operation.duration` | `messaging.operation.name=send` |
| `messaging.receive.duration` | `messaging.client.operation.duration` | `messaging.operation.name=poll` |
| `messaging.publish.messages` | `messaging.client.sent.messages` | Producer send attempts |
| `messaging.receive.messages` | `messaging.client.consumed.messages` | Records delivered to the application, including records reported by a `ConsumeException` |

The shared duration histogram uses seconds (`s`); both message counters use `{message}`. Do not combine send and poll points into a single latency distribution. Select `messaging.operation.name=send` for producer latency and `messaging.operation.name=poll` for consumer poll latency. The corresponding `messaging.operation.type` values are `send` and `receive`, respectively; `poll` is an operation **name**, not an operation type.

For example, a backend query for the old `messaging.receive.duration` should select the `messaging.client.operation.duration` histogram from scope `OpenTelemetry.Instrumentation.ConfluentKafka`, filter to `messaging.operation.name=poll`, and retain its existing topic and consumer-group dimensions using the renamed attributes below. Exporters can normalize dotted metric and attribute names; use the actual names exposed by your backend.

Completed empty/timeout and partition-EOF polls contribute to poll duration but do not increment the consumed-message counter or create poll spans. A canceled poll is not recorded as a successful empty poll. A `ConsumeException` records an error duration; it increments the consumed counter only when the exception includes a consumer record. Poll duration includes time spent waiting for a message, so it is not message-handler processing time.

The old default metric names are not emitted as aliases. During a rolling upgrade, account for both old and new application versions in queries without double-counting measurements.

## Attributes

Update span filters, metric dimensions, and grouping expressions:

| Previous attribute | Replacement | Notes |
| --- | --- | --- |
| `messaging.operation` | `messaging.operation.name` and `messaging.operation.type` | Producer: `send`/`send`; consumer poll: `poll`/`receive`; processing: `process`/`process` |
| `messaging.client_id` | `messaging.client.id` | Applies to the default instrumentation, not the legacy statistics meter |
| `messaging.kafka.consumer.group` | `messaging.consumer.group.name` | Consumer group |
| `messaging.kafka.destination.partition` | `messaging.destination.partition.id` | Changes from a number to a string; update numeric comparisons |
| `messaging.kafka.message.offset` | `messaging.kafka.offset` | Kafka offset |
| `messaging.kafka.message.key` | Same name | Now a canonical string for supported keys; omitted for keys without an unambiguous representation, such as byte arrays |

`messaging.system=kafka` and `messaging.destination.name` continue to identify Kafka and the topic. `error.type` uses a Kafka error-code name for `ProduceException`/`ConsumeException`, or an exception type name for other instrumented failures. Error spans retain their error status but no longer copy exception messages into the status description; use `error.type` rather than matching description text.

The additive `messaging.kafka.cluster.id` span attribute is discovered asynchronously on a best-effort basis. Early spans and spans emitted when discovery fails can omit it. Do not require this attribute to retain all Kafka traffic.

## Spans

The activity source remains `OpenTelemetry.Instrumentation.ConfluentKafka`.

| Previous span | Replacement | Kind |
| --- | --- | --- |
| `<topic> publish` | `send <topic>` | `Producer` (unchanged) |
| `<topic> receive` | `poll <topic>` | Changes from `Consumer` to `Client` |
| `<topic> process` | `process <topic>` | `Consumer` (unchanged) |

Update span-name and span-kind filters together. A filter for consumer-kind receive spans will no longer find polls. Prefer operation attributes over parsing the display name. The Dashboard accepts both the old `messaging.operation` attribute and the new `messaging.operation.name`, with the new attribute taking precedence.

The instrumentation reports version `0.3.0.0` and telemetry schema URL `https://opentelemetry.io/schemas/1.44.0`. A schema URL does not automatically rewrite existing queries or restore old metric names.

## Aspire 8 statistics opt-in

`EnableAspire8ConfluentKafkaMetrics` continues to enable the additional, statistics-based `Aspire.Confluent.Kafka` meter. Its existing schema is preserved: it keeps `messaging.publish.messages`, `messaging.receive.messages`, `messaging.client_id`, the `name`/`type` tags, and its existing units. Other legacy queue, network, and byte metrics are unchanged.

This switch **does not restore the old default OpenTelemetry instrumentation**: the modern meter and spans still use the new conventions. The statistics meter has no equivalent of the old producer/consumer duration histograms, so it is not a complete compatibility mode.

Legacy message counters come from librdkafka's broker-level `txmsgs`/`rxmsgs` statistics, not producer send attempts or deliveries at the `Consume` boundary. Queuing, prefetching, and failures can make them differ from the modern counters. Do not rename those statistics to `messaging.client.sent.messages`/`messaging.client.consumed.messages`, sum the two meters together, or treat their values as interchangeable. Include instrumentation scope in queries during migration. `DisableMetrics=true` still disables the integration's metrics, including its legacy statistics collection.

## References

- [Kafka library usage and configuration](../src/Components/Aspire.Confluent.Kafka/README.md)
- [Upstream instrumentation changelog](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/Instrumentation.ConfluentKafka-0.3.0-alpha.4/src/OpenTelemetry.Instrumentation.ConfluentKafka/CHANGELOG.md)
- [OpenTelemetry v1.44.0 messaging metrics](https://github.com/open-telemetry/semantic-conventions/blob/v1.44.0/docs/messaging/messaging-metrics.md)
- [OpenTelemetry v1.44.0 Kafka conventions](https://github.com/open-telemetry/semantic-conventions/blob/v1.44.0/docs/messaging/kafka.md)
- [librdkafka statistics](https://github.com/confluentinc/librdkafka/blob/master/STATISTICS.md)
