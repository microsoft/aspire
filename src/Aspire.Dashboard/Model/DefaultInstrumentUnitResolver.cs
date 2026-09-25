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
    public string ResolveDisplayedUnit(OtlpInstrumentSummary instrument, bool titleCase, bool pluralize)
    {
        if (!string.IsNullOrEmpty(instrument.Unit))
        {
            var (unit, isRateUnit) = OtlpUnits.GetUnit(instrument.Unit.TrimStart('{').TrimEnd('}'));

            // Don't pluralize rate units, e.g. We want "Bytes per second", not "Bytes per seconds".
            if (pluralize && !isRateUnit)
            {
                unit = unit.Pluralize();
            }

            if (titleCase)
            {
                unit = unit.Titleize();
            }

            return unit;
        }

        // Hard code for instrument names that don't have units
        // but have a descriptive name that lets us infer the unit.
        if (instrument.Name.EndsWith(".count", StringComparison.Ordinal))
        {
            return UntitleCase(loc[nameof(ControlsStrings.PlotlyChartCount)], titleCase);
        }
        else if (instrument.Name.EndsWith(".length", StringComparison.Ordinal))
        {
            return UntitleCase(loc[nameof(ControlsStrings.PlotlyChartLength)], titleCase);
        }
        else
        {
            return UntitleCase(loc[nameof(ControlsStrings.PlotlyChartValue)], titleCase);
        }

        static string UntitleCase(string value, bool titleCase)
        {
            if (!titleCase)
            {
                value = value.ToLower(CultureInfo.CurrentCulture);
            }
            return value;
        }
    }
}
