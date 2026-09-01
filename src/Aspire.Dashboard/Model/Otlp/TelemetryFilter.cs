// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model.Otlp;

public abstract class TelemetryFilter : IEquatable<TelemetryFilter>
{
    public bool Enabled { get; set; } = true;

    public abstract bool Equals(TelemetryFilter? other);
}

[DebuggerDisplay("{DebuggerDisplayText,nq}")]
public class FieldTelemetryFilter : TelemetryFilter
{
    public string Field { get; set; } = default!;
    public string? FallbackField { get; set; }
    public FilterCondition Condition { get; set; }
    public string Value { get; set; } = default!;

    private string DebuggerDisplayText => $"{Field} {ConditionToString(Condition, null)} {Value}";

    public string GetDisplayText(IStringLocalizer<StructuredFiltering> loc) => $"{ResolveFieldName(Field)} {ConditionToString(Condition, loc)} {Value}";

    public static string ResolveFieldName(string name)
    {
        return name switch
        {
            KnownStructuredLogFields.MessageField => "Message",
            KnownStructuredLogFields.TraceIdField => "TraceId",
            KnownStructuredLogFields.SpanIdField => "SpanId",
            KnownStructuredLogFields.OriginalFormatField => "OriginalFormat",
            KnownStructuredLogFields.CategoryField => "Category",
            KnownStructuredLogFields.EventNameField => "EventName",
            KnownStructuredLogFields.TimestampField => "Timestamp",
            KnownTraceFields.NameField => "Name",
            KnownTraceFields.SpanIdField => "SpanId",
            KnownTraceFields.TraceIdField => "TraceId",
            KnownTraceFields.KindField => "Kind",
            KnownTraceFields.StatusField => "Status",
            KnownTraceFields.DurationField => "Duration (ms)",
            KnownTraceFields.TimestampField => "Timestamp",
            KnownSourceFields.NameField => "Source",
            KnownResourceFields.ServiceNameField => "Resource",
            _ => name
        };
    }

    public static bool IsNumericField(string name) => name is KnownTraceFields.DurationField;

    /// <summary>
    /// Returns true when the field represents a timestamp that should be compared as a date.
    /// Filter values for these fields are parsed as <see cref="DateTime"/> and compared using
    /// milliseconds stored in the field value from <see cref="OtlpSpan.GetFieldValue"/> / <see cref="OtlpLogEntry.GetFieldValue"/>.
    /// </summary>
    public static bool IsDateField(string name) => name is KnownTraceFields.TimestampField or KnownStructuredLogFields.TimestampField;

    internal static FieldType GetFieldType(string name)
    {
        if (IsNumericField(name))
        {
            return FieldType.Numeric;
        }
        if (IsDateField(name))
        {
            return FieldType.Date;
        }
        return FieldType.String;
    }

    public static string ConditionToString(FilterCondition c, IStringLocalizer<StructuredFiltering>? loc) =>
        c switch
        {
            FilterCondition.Equals => "==",
            FilterCondition.Contains => loc?[nameof(StructuredFiltering.ConditionContains)] ?? "contains",
            FilterCondition.GreaterThan => ">",
            FilterCondition.LessThan => "<",
            FilterCondition.GreaterThanOrEqual => ">=",
            FilterCondition.LessThanOrEqual => "<=",
            FilterCondition.NotEqual => "!=",
            FilterCondition.NotContains => loc?[nameof(StructuredFiltering.ConditionNotContains)] ?? "not contains",
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, null)
        };

    public override bool Equals(TelemetryFilter? other)
    {
        var otherFilter = other as FieldTelemetryFilter;
        if (otherFilter == null)
        {
            return false;
        }

        if (Field != otherFilter.Field)
        {
            return false;
        }

        if (Condition != otherFilter.Condition)
        {
            return false;
        }

        if (!string.Equals(Value, otherFilter.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

internal enum FieldType
{
    String,
    Numeric,
    Date
}
