// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model.Otlp;

public abstract class TelemetryFilter : IEquatable<TelemetryFilter>
{
    public bool Enabled { get; set; } = true;

    public abstract bool Equals(TelemetryFilter? other);

    public abstract IEnumerable<OtlpLogEntry> Apply(IEnumerable<OtlpLogEntry> input);

    public abstract bool Apply(OtlpSpan span);
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

    private static Func<string?, string, bool> ConditionToFuncString(FilterCondition c) =>
        c switch
        {
            FilterCondition.Equals => (a, b) => string.Equals(a, b, StringComparisons.OtlpFieldValue),
            FilterCondition.Contains => (a, b) => a != null && a.Contains(b, StringComparisons.OtlpFieldValue),
            // Comparison operators are only meaningful for numeric fields. For string fields,
            // never match — following the same approach as GitHub search, which only allows
            // comparison operators on known numeric qualifiers.
            FilterCondition.GreaterThan => static (a, b) => false,
            FilterCondition.LessThan => static (a, b) => false,
            FilterCondition.GreaterThanOrEqual => static (a, b) => false,
            FilterCondition.LessThanOrEqual => static (a, b) => false,
            FilterCondition.NotEqual => (a, b) => !string.Equals(a, b, StringComparisons.OtlpFieldValue),
            FilterCondition.NotContains => (a, b) => a != null && !a.Contains(b, StringComparisons.OtlpFieldValue),
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, null)
        };

    private static Func<DateTime, DateTime, bool> ConditionToFuncDate(FilterCondition c) =>
        c switch
        {
            FilterCondition.Equals => (a, b) => a == b,
            //Condition.Contains => (a, b) => a.Contains(b),
            FilterCondition.GreaterThan => (a, b) => a > b,
            FilterCondition.LessThan => (a, b) => a < b,
            FilterCondition.GreaterThanOrEqual => (a, b) => a >= b,
            FilterCondition.LessThanOrEqual => (a, b) => a <= b,
            FilterCondition.NotEqual => (a, b) => a != b,
            //Condition.NotContains => (a, b) => !a.Contains(b),
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, null)
        };

    private static Func<double, double, bool> ConditionToFuncNumber(FilterCondition c) =>
        c switch
        {
            FilterCondition.Equals => (a, b) => a == b,
            //Condition.Contains => (a, b) => a.Contains(b),
            FilterCondition.GreaterThan => (a, b) => a > b,
            FilterCondition.LessThan => (a, b) => a < b,
            FilterCondition.GreaterThanOrEqual => (a, b) => a >= b,
            FilterCondition.LessThanOrEqual => (a, b) => a <= b,
            FilterCondition.NotEqual => (a, b) => a != b,
            //Condition.NotContains => (a, b) => !a.Contains(b),
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, null)
        };

    private static bool TryMatchNumber(string fieldValue, string filterValue, FilterCondition condition)
    {
        if (!double.TryParse(fieldValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldNumber) ||
            !double.TryParse(filterValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var filterNumber) ||
            !double.IsFinite(fieldNumber) ||
            !double.IsFinite(filterNumber))
        {
            return false;
        }

        if (condition is not (FilterCondition.Equals or FilterCondition.GreaterThan or FilterCondition.LessThan or FilterCondition.GreaterThanOrEqual or FilterCondition.LessThanOrEqual or FilterCondition.NotEqual))
        {
            return false;
        }

        var func = ConditionToFuncNumber(condition);
        return func(fieldNumber, filterNumber);
    }

    /// <summary>
    /// Compares a field value (stored as milliseconds since DateTime.MinValue) against a filter value (a date string).
    /// Milliseconds fit within double's exact integer range (max ~3.16×10^14 vs 2^53 ≈ 9×10^15).
    /// </summary>
    private static bool TryMatchDate(string fieldMillisecondsValue, string filterDateValue, FilterCondition condition)
    {
        if (!long.TryParse(fieldMillisecondsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fieldMs))
        {
            return false;
        }

        if (!DateTime.TryParse(filterDateValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var filterDate))
        {
            return false;
        }

        if (condition is not (FilterCondition.Equals or FilterCondition.GreaterThan or FilterCondition.LessThan or FilterCondition.GreaterThanOrEqual or FilterCondition.LessThanOrEqual or FilterCondition.NotEqual))
        {
            return false;
        }

        var filterMs = filterDate.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
        var func = ConditionToFuncNumber(condition);
        return func(fieldMs, filterMs);
    }

    public bool HasNumericMatch(double fieldValue)
    {
        if (!double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var filterNumber) ||
            !double.IsFinite(filterNumber) ||
            !double.IsFinite(fieldValue))
        {
            return false;
        }

        if (Condition is not (FilterCondition.Equals or FilterCondition.GreaterThan or FilterCondition.LessThan or FilterCondition.GreaterThanOrEqual or FilterCondition.LessThanOrEqual or FilterCondition.NotEqual))
        {
            return false;
        }

        var func = ConditionToFuncNumber(Condition);
        return func(fieldValue, filterNumber);
    }

    public override IEnumerable<OtlpLogEntry> Apply(IEnumerable<OtlpLogEntry> input)
    {
        switch (Field)
        {
            case nameof(OtlpLogEntry.TimeStamp):
                {
                    var date = DateTime.Parse(Value, CultureInfo.InvariantCulture);
                    var func = ConditionToFuncDate(Condition);
                    return input.Where(x => func(x.TimeStamp, date));
                }
            case nameof(OtlpLogEntry.Severity):
                {
                    if (Enum.TryParse<LogLevel>(Value, ignoreCase: true, out var value))
                    {
                        var func = ConditionToFuncNumber(Condition);
                        return input.Where(x => func((int)x.Severity, (double)value));
                    }
                    return input;
                }
            case nameof(OtlpLogEntry.Message):
                {
                    var func = ConditionToFuncString(Condition);
                    return input.Where(x => func(x.Message, Value));
                }
            default:
                {
                    var fieldType = GetFieldType(Field);
                    return input.Where(x =>
                    {
                        var fieldValue = OtlpLogEntry.GetFieldValue(x, Field) ?? string.Empty;
                        return fieldType switch
                        {
                            FieldType.Numeric => TryMatchNumber(fieldValue, Value, Condition),
                            FieldType.Date => TryMatchDate(fieldValue, Value, Condition),
                            _ => ConditionToFuncString(Condition)(fieldValue, Value)
                        };
                    });
                }
        }
    }

    public override bool Apply(OtlpSpan span)
    {
        var fieldValues = OtlpSpan.GetFieldValue(span, Field);
        var isNot = Condition is FilterCondition.NotEqual or FilterCondition.NotContains;
        var fieldType = GetFieldType(Field);

        if (!isNot)
        {
            // Or
            if (fieldValues.Value1 != null && IsMatch(fieldValues.Value1, Value, Condition, fieldType))
            {
                return true;
            }
            if (fieldValues.Value2 != null && IsMatch(fieldValues.Value2, Value, Condition, fieldType))
            {
                return true;
            }
        }
        else
        {
            // And — both values must satisfy the not-equal/not-contains condition.
            // When Value2 is null (most fields only have one value), Value1 alone is sufficient.
            if (fieldValues.Value1 != null && IsMatch(fieldValues.Value1, Value, Condition, fieldType))
            {
                if (fieldValues.Value2 is null || IsMatch(fieldValues.Value2, Value, Condition, fieldType))
                {
                    return true;
                }
            }
        }

        return false;

        static bool IsMatch(string fieldValue, string filterValue, FilterCondition condition, FieldType fieldType)
        {
            return fieldType switch
            {
                FieldType.Numeric => TryMatchNumber(fieldValue, filterValue, condition),
                FieldType.Date => TryMatchDate(fieldValue, filterValue, condition),
                _ => ConditionToFuncString(condition)(fieldValue, filterValue)
            };
        }
    }

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
