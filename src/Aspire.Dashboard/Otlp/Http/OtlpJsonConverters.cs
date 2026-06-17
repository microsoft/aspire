// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Otlp.Model.Serialization;
using Aspire.Otlp.Serialization;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Aspire.Dashboard.Otlp.Http;

internal static class OtlpJsonConverters
{
    private static readonly Dictionary<Type, Func<string, IMessage>> s_jsonDeserializers = new()
    {
        [typeof(ExportTraceServiceRequest)] = json =>
        {
            var jsonObj = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpExportTraceServiceRequestJson);
            return jsonObj is null ? null! : OtlpJsonToProtobufConverter.ToProtobuf(jsonObj);
        },
        [typeof(ExportLogsServiceRequest)] = json =>
        {
            var jsonObj = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpExportLogsServiceRequestJson);
            return jsonObj is null ? null! : OtlpJsonToProtobufConverter.ToProtobuf(jsonObj);
        },
        [typeof(ExportMetricsServiceRequest)] = json =>
        {
            var jsonObj = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpExportMetricsServiceRequestJson);
            return jsonObj is null ? null! : OtlpJsonToProtobufConverter.ToProtobuf(jsonObj);
        }
    };

    private static readonly Dictionary<Type, Func<IMessage, string>> s_jsonSerializers = new()
    {
        [typeof(ExportTraceServiceResponse)] = message =>
        {
            var json = OtlpProtobufToJsonConverter.ToJson((ExportTraceServiceResponse)message);
            return JsonSerializer.Serialize(json, OtlpJsonSerializerContext.Default.OtlpExportTraceServiceResponseJson);
        },
        [typeof(ExportLogsServiceResponse)] = message =>
        {
            var json = OtlpProtobufToJsonConverter.ToJson((ExportLogsServiceResponse)message);
            return JsonSerializer.Serialize(json, OtlpJsonSerializerContext.Default.OtlpExportLogsServiceResponseJson);
        },
        [typeof(ExportMetricsServiceResponse)] = message =>
        {
            var json = OtlpProtobufToJsonConverter.ToJson((ExportMetricsServiceResponse)message);
            return JsonSerializer.Serialize(json, OtlpJsonSerializerContext.Default.OtlpExportMetricsServiceResponseJson);
        }
    };

    public static TMessage? DeserializeJson<TMessage>(string json) where TMessage : IMessage
    {
        if (!s_jsonDeserializers.TryGetValue(typeof(TMessage), out var deserializer))
        {
            throw new NotSupportedException($"JSON deserialization for type {typeof(TMessage).Name} is not supported.");
        }

        var result = deserializer(json);
        return result is null ? default : (TMessage)result;
    }

    public static string SerializeJson<TMessage>(TMessage message) where TMessage : IMessage
    {
        if (!s_jsonSerializers.TryGetValue(typeof(TMessage), out var serializer))
        {
            throw new NotSupportedException($"JSON serialization for type {typeof(TMessage).Name} is not supported.");
        }

        return serializer(message);
    }
}
