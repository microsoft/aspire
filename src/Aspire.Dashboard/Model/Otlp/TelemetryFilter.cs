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
            KnownTraceFields.NameField => "Name",
            KnownTraceFields.SpanIdField => "SpanId",
            KnownTraceFields.TraceIdField => "TraceId",
            KnownTraceFields.KindField => "Kind",
            KnownTraceFields.StatusField => "Status",
            KnownTraceFields.DurationField => "Duration (ms)",
            KnownSourceFields.NameField => "Source",
            KnownResourceFields.ServiceNameField => "Resource",
            _ => name
        };
    }

    public static bool IsNumericField(string name) => name is KnownTraceFields.DurationField;

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
            // Condition.GreaterThan => (a, b) => a > b,
            // Condition.LessThan => (a, b) => a < b,
            // Condition.GreaterThanOrEqual => (a, b) => a >= b,
            // Condition.LessThanOrEqual => (a, b) => a <= b,
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
                    var func = ConditionToFuncString(Condition);
                    return input.Where(x => func(OtlpLogEntry.GetFieldValue(x, Field) ?? string.Empty, Value));
                }
        }
    }

    public override bool Apply(OtlpSpan span)
    {
        var fieldValue = OtlpSpan.GetFieldValue(span, Field);
        var isNot = Condition is FilterCondition.NotEqual or FilterCondition.NotContains;
        var isNumeric = IsNumericField(Field);

        if (!isNot)
        {
            // Or
            if (fieldValue.Value1 != null && IsMatch(fieldValue.Value1, Value, Condition, isNumeric))
            {
                return true;
            }
            if (fieldValue.Value2 != null && IsMatch(fieldValue.Value2, Value, Condition, isNumeric))
            {
                return true;
            }
        }
        else
        {
            // And — both values must satisfy the not-equal/not-contains condition.
            // When Value2 is null (most fields only have one value), Value1 alone is sufficient.
            if (fieldValue.Value1 != null && IsMatch(fieldValue.Value1, Value, Condition, isNumeric))
            {
                if (fieldValue.Value2 is null || IsMatch(fieldValue.Value2, Value, Condition, isNumeric))
                {
                    return true;
                }
            }
        }

        return false;

        static bool IsMatch(string fieldValue, string filterValue, FilterCondition condition, bool isNumeric)
        {
            if (isNumeric)
            {
                return TryMatchNumber(fieldValue, filterValue, condition);
            }

            var func = ConditionToFuncString(condition);
            return func(fieldValue, filterValue);
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
