// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Protocols available for OTLP exporters.
/// </summary>
public enum OtlpProtocol
{
    /// <summary>
    /// A gRPC-based OTLP exporter.
    /// </summary>
    Grpc,

    /// <summary>
    /// Http/Protobuf-based OTLP exporter.
    /// </summary>
    HttpProtobuf,

    /// <summary>
    /// Http/JSON-based OTLP exporter.
    /// </summary>
    HttpJson
}
