// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Aspire.Confluent.Kafka.ConfluentKafkaMetrics;

namespace Aspire.Confluent.Kafka;

internal sealed partial class MetricsService(MetricsChannel channel, ConfluentKafkaMetrics metrics, ILogger<MetricsService> logger) : BackgroundService
{
    private readonly Dictionary<string, Statistics> _state = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var json))
            {
                Statistics? statistics;
                try
                {
                    statistics = JsonSerializer.Deserialize(json, StatisticsJsonSerializerContext.Default.Statistics);
                }
                catch
                {
                    LogDeserializationWarning(logger, json);
                    continue;
                }

                if (statistics == null || statistics.Name == null)
                {
                    LogDeserializationWarning(logger, json);
                    continue;
                }

                TagList tags = new()
                {
                    { Tags.ClientId, statistics.ClientId },
                    { Tags.Name, statistics.Name }
                };

                metrics.ReplyQueueMeasurements.Enqueue(new Measurement<long>(statistics.ReplyQueue, tags));
                metrics.MessageCountMeasurements.Enqueue(new Measurement<long>(statistics.MessageCount, tags));
                metrics.MessageSizeMeasurements.Enqueue(new Measurement<long>(statistics.MessageSize, tags));

                tags.Add(new KeyValuePair<string, object?> (Tags.Type, statistics.Type));

                if (_state.TryGetValue(statistics.Name, out var previous))
                {
                    metrics.Tx.Add(statistics.Tx - previous.Tx, tags);
                    metrics.TxBytes.Add(statistics.TxBytes - previous.TxBytes, tags);
                    metrics.TxMessages.Add(statistics.TxMessages - previous.TxMessages, tags);
                    metrics.TxMessageBytes.Add(statistics.TxMessageBytes - previous.TxMessageBytes, tags);
                    metrics.Rx.Add(statistics.Rx - previous.Rx, tags);
                    metrics.RxBytes.Add(statistics.RxBytes - previous.RxBytes, tags);
                    metrics.RxMessages.Add(statistics.RxMessages - previous.RxMessages, tags);
                    metrics.RxMessageBytes.Add(statistics.RxMessageBytes - previous.RxMessageBytes, tags);
                }
                else
                {
                    metrics.Tx.Add(statistics.Tx, tags);
                    metrics.TxBytes.Add(statistics.TxBytes, tags);
                    metrics.TxMessages.Add(statistics.TxMessages, tags);
                    metrics.TxMessageBytes.Add(statistics.TxMessageBytes, tags);
                    metrics.Rx.Add(statistics.Rx, tags);
                    metrics.RxBytes.Add(statistics.RxBytes, tags);
                    metrics.RxMessages.Add(statistics.RxMessages, tags);
                    metrics.RxMessageBytes.Add(statistics.RxMessageBytes, tags);
                }

                _state[statistics.Name] = statistics;
            }
        }
    }

    [LoggerMessage(LogLevel.Warning, EventId = 1, Message = "Invalid statistics json payload received: '{json}'")]
    private static partial void LogDeserializationWarning(ILogger logger, string json);
}
