// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Otlp;

public static class KnownStructuredLogFields
{
    public const string MessageField = "log.message";
    public const string CategoryField = "log.category";
    public const string TraceIdField = "log.traceid";
    public const string SpanIdField = "log.spanid";
    public const string ParentIdField = "log.parentid";
    public const string LevelField = "log.level";
    public const string OriginalFormatField = "log.originalformat";
    public const string EventNameField = "log.eventname";

    public static readonly List<string> AllFields = [
        MessageField,
        CategoryField,
        KnownResourceFields.ServiceNameField,
        TraceIdField,
        SpanIdField,
        OriginalFormatField,
        EventNameField
    ];
}
