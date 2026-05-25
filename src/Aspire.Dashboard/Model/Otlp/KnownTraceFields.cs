// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Otlp;

public static class KnownTraceFields
{
    public const string NameField = "trace.name";
    public const string KindField = "trace.kind";
    public const string StatusField = "trace.status";
    public const string TraceIdField = "trace.traceid";
    public const string SpanIdField = "trace.spanid";
    public const string DurationField = "trace.duration";

    // Not used in search.
    public const string StatusMessageField = "trace.statusmessage";
    public const string ParentIdField = "trace.parentid";
    public const string DestinationField = "trace.destination";

    public static readonly List<string> AllFields = [
        NameField,
        KindField,
        StatusField,
        KnownResourceFields.ServiceNameField,
        TraceIdField,
        SpanIdField,
        KnownSourceFields.NameField,
        DurationField
    ];
}
