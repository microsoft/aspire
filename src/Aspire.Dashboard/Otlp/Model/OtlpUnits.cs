// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Dashboard.Otlp.Model;

public static class OtlpUnits
{
    public static (string Unit, bool IsRateUnit, bool StartsWithExpandedUnit) GetUnit(string unit)
    {
        // Dropping the portions of the Unit within brackets (e.g. {packet}). Brackets MUST NOT be included in the resulting unit. A "count of foo" is considered unitless in Prometheus.
        // https://github.com/open-telemetry/opentelemetry-specification/blob/b2f923fb1650dde1f061507908b834035506a796/specification/compatibility/prometheus_and_openmetrics.md#L238
        var updatedUnit = RemoveAnnotations(unit);

        // Converting "foo/bar" to "foo_per_bar".
        // https://github.com/open-telemetry/opentelemetry-specification/blob/b2f923fb1650dde1f061507908b834035506a796/specification/compatibility/prometheus_and_openmetrics.md#L240C3-L240C41
        var slashIndex = updatedUnit.IndexOf('/');
        if (slashIndex >= 0 && slashIndex < updatedUnit.Length - 1)
        {
            var numerator = updatedUnit.AsSpan(0, slashIndex);
            var expandedNumerator = MapUnit(numerator);
            var denominator = MapPerUnit(updatedUnit.AsSpan(slashIndex + 1));
            return (expandedNumerator + " per " + denominator, true, !numerator.SequenceEqual(expandedNumerator));
        }

        // Converting from abbreviations to full words (e.g. "ms" to "milliseconds").
        // https://github.com/open-telemetry/opentelemetry-specification/blob/b2f923fb1650dde1f061507908b834035506a796/specification/compatibility/prometheus_and_openmetrics.md#L237
        var expandedUnit = MapUnit(updatedUnit);
        return (expandedUnit, false, expandedUnit != updatedUnit);
    }

    public static string RemoveAnnotations(string unit)
    {
        // UCUM standard says the curly braces shouldn't be nested:
        // https://ucum.org/ucum#section-Character-Set-and-Lexical-Rules
        // What should happen if they are nested isn't defined.
        // Right now the remove annotations code doesn't attempt to balance multiple start and end braces.
        StringBuilder? sb = null;

        var hasOpenBrace = false;
        var startOpenBraceIndex = 0;
        var lastWriteIndex = 0;

        for (var i = 0; i < unit.Length; i++)
        {
            var c = unit[i];
            if (c == '{')
            {
                if (!hasOpenBrace)
                {
                    hasOpenBrace = true;
                    startOpenBraceIndex = i;
                }
            }
            else if (c == '}')
            {
                if (hasOpenBrace)
                {
                    sb ??= new StringBuilder();
                    sb.Append(unit, lastWriteIndex, startOpenBraceIndex - lastWriteIndex);
                    hasOpenBrace = false;
                    lastWriteIndex = i + 1;
                }
            }
        }

        if (lastWriteIndex == 0)
        {
            return unit;
        }

        sb!.Append(unit, lastWriteIndex, unit.Length - lastWriteIndex);
        return sb.ToString();
    }

    // OTLP metrics use the c/s notation as specified at https://ucum.org/ucum.html
    // (See also https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/metrics/semantic_conventions/README.md#instrument-units)
    // OpenMetrics specification for units: https://github.com/OpenObservability/OpenMetrics/blob/main/specification/OpenMetrics.md#units-and-base-units
    public static string MapUnit(ReadOnlySpan<char> unit)
    {
        return unit switch
        {
            // Time
            "d" => "days",
            "h" => "hours",
            "min" => "minutes",
            "s" => "seconds",
            "ms" => "milliseconds",
            "us" => "microseconds",
            "ns" => "nanoseconds",

            // Bytes
            "By" => "bytes",
            "KiBy" => "kibibytes",
            "MiBy" => "mebibytes",
            "GiBy" => "gibibytes",
            "TiBy" => "tibibytes",
            "KBy" => "kilobytes",
            "MBy" => "megabytes",
            "GBy" => "gigabytes",
            "TBy" => "terabytes",
            "B" => "bytes",
            "KB" => "kilobytes",
            "MB" => "megabytes",
            "GB" => "gigabytes",
            "TB" => "terabytes",

            // SI
            "m" => "meters",
            "V" => "volts",
            "A" => "amperes",
            "J" => "joules",
            "W" => "watts",
            "g" => "grams",

            // Misc
            "Cel" => "celsius",
            "Hz" => "hertz",
            "1" => string.Empty,
            "%" => "percent",
            "$" => "dollars",
            _ => unit.ToString(),
        };
    }

    // The map that translates the "per" unit
    // Example: s => per second (singular)
    public static string MapPerUnit(ReadOnlySpan<char> perUnit)
    {
        return perUnit switch
        {
            "s" => "second",
            "m" => "minute",
            "h" => "hour",
            "d" => "day",
            "w" => "week",
            "mo" => "month",
            "y" => "year",
            _ => perUnit.ToString(),
        };
    }
}
