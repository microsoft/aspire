// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Authentication;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Aspire.Dashboard.Otlp.Http;

public static class OtlpHttpEndpointsBuilder
{
    public const string ProtobufContentType = "application/x-protobuf";
    public const string JsonContentType = "application/json";
    public const string CorsPolicyName = "OtlpHttpCors";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record StatusResponse(int Code, string Message);

    public static void MapHttpOtlpApi(this IEndpointRouteBuilder endpoints, OtlpOptions options)
    {
        var httpEndpoint = options.GetHttpEndpointAddress();
        if (httpEndpoint == null)
        {
            // Don't map OTLP HTTP route endpoints if there isn't a Kestrel endpoint to access them with.
            // Note that we don't need to add map routing endpoints that return 404 here because they'll always return Unauthorized from another Kestrel endpoint.
            return;
        }

        var group = endpoints
            .MapGroup("/v1")
            .AddOtlpHttpMetadata();

        if (!string.IsNullOrEmpty(options.Cors.AllowedOrigins))
        {
            group = group.RequireCors(CorsPolicyName);
        }

        group.MapPost("logs", static (MessageBindable<ExportLogsServiceRequest> request, OtlpLogsService service) =>
        {
            if (request.Message is null)
            {
                return Results.Empty;
            }
            return OtlpResult.Response(service.Export(request.Message), request.RequestContentType);
        });
        group.MapPost("traces", static (MessageBindable<ExportTraceServiceRequest> request, OtlpTraceService service) =>
        {
            if (request.Message is null)
            {
                return Results.Empty;
            }
            return OtlpResult.Response(service.Export(request.Message), request.RequestContentType);
        });
        group.MapPost("metrics", (MessageBindable<ExportMetricsServiceRequest> request, OtlpMetricsService service) =>
        {
            if (request.Message is null)
            {
                return Results.Empty;
            }
            return OtlpResult.Response(service.Export(request.Message), request.RequestContentType);
        });
    }

    internal enum KnownContentType
    {
        None,
        Protobuf,
        Json
    }

    private static KnownContentType GetKnownContentType(string? contentType, out StringSegment charSet)
    {
        if (contentType != null && MediaTypeHeaderValue.TryParse(contentType, out var mt))
        {
            if (string.Equals(mt.MediaType, JsonContentType, StringComparison.OrdinalIgnoreCase))
            {
                charSet = mt.CharSet;
                return KnownContentType.Json;
            }

            if (string.Equals(mt.MediaType, ProtobufContentType, StringComparison.OrdinalIgnoreCase))
            {
                charSet = mt.CharSet;
                return KnownContentType.Protobuf;
            }
        }

        charSet = default;
        return KnownContentType.None;
    }

    private static async Task WriteUnsupportedContentTypeResponse(HttpContext httpContext, ILogger logger)
    {
        logger.LogDebug("OTLP HTTP request with unsupported content type '{ContentType}' was rejected. Only '{SupportedContentType}' is supported.", httpContext.Request.ContentType, ProtobufContentType);

        httpContext.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
        httpContext.Response.ContentType = JsonContentType;

        var status = new StatusResponse(
            Code: 15, // UNIMPLEMENTED from gRPC status codes
            Message: $"Content type '{httpContext.Request.ContentType}' is not supported. Only '{ProtobufContentType}' is supported.");

        var json = JsonSerializer.Serialize(status, s_jsonOptions);
        await httpContext.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
    }

    private static T AddOtlpHttpMetadata<T>(this T builder) where T : IEndpointConventionBuilder
    {
        builder
            .RequireAuthorization(OtlpAuthorization.PolicyName)
            .SkipStatusCodePages();
        return builder;
    }

    private sealed class MessageBindable<TMessage> : IBindableFromHttpContext<MessageBindable<TMessage>> where TMessage : IMessage<TMessage>, new()
    {
        public static readonly MessageBindable<TMessage> Empty = new MessageBindable<TMessage>() { Logger = NullLogger.Instance };

        public TMessage? Message { get; private set; }

        public KnownContentType RequestContentType { get; private set; }

        public required ILogger Logger { get; init; }

        public static async ValueTask<MessageBindable<TMessage>?> BindAsync(HttpContext context, ParameterInfo parameter)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aspire.Dashboard.Otlp.Http");

            var contentType = GetKnownContentType(context.Request.ContentType, out var charSet);
            switch (contentType)
            {
                case KnownContentType.Protobuf:
                    try
                    {
                        var message = await ReadOtlpData(context, data =>
                        {
                            var message = new TMessage();
                            message.MergeFrom(data);
                            return message;
                        }).ConfigureAwait(false);

                        return new MessageBindable<TMessage> { Message = message, RequestContentType = contentType, Logger = logger };
                    }
                    catch (BadHttpRequestException ex)
                    {
                        context.Response.StatusCode = ex.StatusCode;
                        return Empty;
                    }
                case KnownContentType.Json:
                    try
                    {
                        var message = await ReadOtlpJsonData<TMessage>(context).ConfigureAwait(false);
                        return new MessageBindable<TMessage> { Message = message, RequestContentType = contentType, Logger = logger };
                    }
                    catch (JsonException ex)
                    {
                        logger.LogDebug(ex, "Failed to deserialize OTLP JSON request.");
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return Empty;
                    }
                    catch (BadHttpRequestException ex)
                    {
                        context.Response.StatusCode = ex.StatusCode;
                        return Empty;
                    }
                default:
                    await WriteUnsupportedContentTypeResponse(context, logger).ConfigureAwait(false);
                    return Empty;
            }
        }
    }

    private static async Task<TMessage?> ReadOtlpJsonData<TMessage>(HttpContext httpContext) where TMessage : IMessage<TMessage>, new()
    {
        var json = await ReadOtlpData(httpContext, data =>
        {
            // Convert the buffer to a string for JSON parsing
            if (data.IsSingleSegment)
            {
                return Encoding.UTF8.GetString(data.FirstSpan);
            }
            else
            {
                var bytes = data.ToArray();
                return Encoding.UTF8.GetString(bytes);
            }
        }).ConfigureAwait(false);

        return OtlpJsonConverters.DeserializeJson<TMessage>(json);
    }

    private sealed class OtlpResult<T> : IResult where T : IMessage
    {
        private readonly T _message;
        private readonly KnownContentType _requestContentType;

        public OtlpResult(T message, KnownContentType requestContentType)
        {
            _message = message;
            _requestContentType = requestContentType;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aspire.Dashboard.Otlp.Http");

            switch (_requestContentType)
            {
                case KnownContentType.Protobuf:
                    // This isn't very efficient but OTLP Protobuf responses are small.
                    var ms = new MemoryStream();
                    _message.WriteTo(ms);
                    ms.Seek(0, SeekOrigin.Begin);

                    httpContext.Response.ContentType = ProtobufContentType;
                    await ms.CopyToAsync(httpContext.Response.Body).ConfigureAwait(false);
                    break;
                case KnownContentType.Json:
                    httpContext.Response.ContentType = JsonContentType;
                    var jsonResponse = OtlpJsonConverters.SerializeJson(_message);
                    await httpContext.Response.WriteAsync(jsonResponse, Encoding.UTF8).ConfigureAwait(false);
                    break;
                default:
                    await WriteUnsupportedContentTypeResponse(httpContext, logger).ConfigureAwait(false);
                    break;
            }
        }
    }

    private sealed class OtlpResult
    {
        public static OtlpResult<T> Response<T>(T response, KnownContentType requestContentType) where T : IMessage => new OtlpResult<T>(response, requestContentType);
    }

    private static async Task<T> ReadOtlpData<T>(
        HttpContext httpContext,
        Func<ReadOnlySequence<byte>, T> exporter)
    {
        const int MaxRequestSize = 1024 * 1024 * 4; // 4 MB. Matches default gRPC request limit.

        ReadResult result = default;
        try
        {
            do
            {
                result = await httpContext.Request.BodyReader.ReadAsync().ConfigureAwait(false);

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException("Read call was canceled.");
                }

                if (result.Buffer.Length > MaxRequestSize)
                {
                    // Too big!
                    throw new BadHttpRequestException(
                        $"The request body was larger than the max allowed of {MaxRequestSize} bytes.",
                        StatusCodes.Status400BadRequest);
                }

                if (result.IsCompleted)
                {
                    break;
                }
                else
                {
                    httpContext.Request.BodyReader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                }
            } while (true);

            return exporter(result.Buffer);
        }
        finally
        {
            if (!result.Equals(default(ReadResult)))
            {
                httpContext.Request.BodyReader.AdvanceTo(result.Buffer.End);
            }
        }
    }
}
