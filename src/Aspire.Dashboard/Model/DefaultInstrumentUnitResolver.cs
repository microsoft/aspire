// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Humanizer;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

public sealed class DefaultInstrumentUnitResolver(IStringLocalizer<ControlsStrings> loc) : IInstrumentUnitResolver
{
    public string ResolveDisplayedUnit(OtlpInstrumentSummary instrument, bool capitalize, bool pluralize)
    {
        if (!string.IsNullOrEmpty(instrument.Unit))
        {
            var (unit, isRateUnit) = OtlpUnits.GetUnit(instrument.Unit.TrimStart('{').TrimEnd('}'));
            unit = unit.Replace('_', ' ').ToLowerInvariant();

            // Don't pluralize rate units, e.g. We want "Bytes per second", not "Bytes per seconds".
            if (pluralize && !isRateUnit)
            {
                unit = unit.Pluralize();
            }

            if (capitalize && unit.Length > 0)
            {
                unit = char.ToUpperInvariant(unit[0]) + unit[1..];
            }

            return unit;
        }

        // Hard code for instrument names that don't have units
        // but have a descriptive name that lets us infer the unit.
        if (instrument.Name.EndsWith(".count", StringComparison.Ordinal))
        {
            return FormatLocalizedUnit(loc[nameof(ControlsStrings.PlotlyChartCount)], capitalize);
        }
        else if (instrument.Name.EndsWith(".length", StringComparison.Ordinal))
        {
            return FormatLocalizedUnit(loc[nameof(ControlsStrings.PlotlyChartLength)], capitalize);
        }
        else
        {
            return FormatLocalizedUnit(loc[nameof(ControlsStrings.PlotlyChartValue)], capitalize);
        }

        static string FormatLocalizedUnit(string value, bool capitalize) =>
            capitalize ? value : value.ToLower(CultureInfo.CurrentCulture);
    }
}
