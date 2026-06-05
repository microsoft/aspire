// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Model;

/// <summary>
/// Specifies the relationship between a span and its parent/children in a trace.
/// </summary>
/// <remarks>
/// Values map to <c>OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind</c>.
/// </remarks>
public enum OtlpSpanKind
{
    /// <summary>
    /// Unspecified. Do NOT use as default.
    /// Implementations MAY assume SpanKind to be INTERNAL when receiving UNSPECIFIED.
    /// </summary>
    Unspecified = 0,
    /// <summary>
    /// Indicates that the span represents an internal operation within an application,
    /// as opposed to an operation happening at the boundaries. Default value.
    /// </summary>
    Internal = 1,
    /// <summary>
    /// Indicates that the span covers server-side handling of an RPC or other
    /// remote network request.
    /// </summary>
    Server = 2,
    /// <summary>
    /// Indicates that the span describes a request to some remote service.
    /// </summary>
    Client = 3,
    /// <summary>
    /// Indicates that the span describes a producer sending a message to a broker.
    /// Unlike CLIENT and SERVER, there is often no direct critical path latency relationship
    /// between producer and consumer spans. A PRODUCER span ends when the message was accepted
    /// by the broker while the logical processing of the message might span a much longer time.
    /// </summary>
    Producer = 4,
    /// <summary>
    /// Indicates that the span describes consumer receiving a message from a broker.
    /// Like the PRODUCER kind, there is often no direct critical path latency relationship
    /// between producer and consumer spans.
    /// </summary>
    Consumer = 5,
}
