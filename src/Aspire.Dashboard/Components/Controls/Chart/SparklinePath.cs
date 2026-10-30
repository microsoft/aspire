// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Dashboard.Components.Controls.Chart;

/// <summary>
/// Builds SVG path data for small inline sparklines, as a line or as bars.
/// </summary>
public static class SparklinePath
{
    // Keeps strokes from being clipped at the top and bottom edges of the SVG.
    internal const double Inset = 2;

    /// <summary>
    /// Builds a smoothed line path and a matching closed area path for the values.
    /// Values are spaced evenly across <paramref name="width"/>. Null values create gaps.
    /// Returns empty strings when there is nothing to draw.
    /// </summary>
    public static (string Line, string Area) Build(IReadOnlyList<double?> values, double width, double height)
    {
        if (values.Count < 2 || !TryCreateScale(values, height, out var scale))
        {
            return (string.Empty, string.Empty);
        }

        var step = width / (values.Count - 1);
        var zeroY = scale.ScaleY(0);
        var line = new StringBuilder();
        var area = new StringBuilder();
        var segment = new List<(double X, double Y)>();

        for (var i = 0; i <= values.Count; i++)
        {
            if (i < values.Count && values[i] is { } v)
            {
                segment.Add((i * step, scale.ScaleY(v)));
                continue;
            }

            if (segment.Count > 0)
            {
                AppendSegment(line, area, segment, zeroY);
                segment.Clear();
            }
        }

        return (line.ToString(), area.ToString());
    }

    /// <summary>
    /// Builds a path of vertical bars, one per value, rising from zero. Null and zero values draw no bar.
    /// Returns an empty string when there is nothing to draw.
    /// </summary>
    public static string BuildBars(IReadOnlyList<double?> values, double width, double height)
    {
        if (values.Count == 0 || !TryCreateScale(values, height, out var scale))
        {
            return string.Empty;
        }

        // Each value gets an equal slot; the gap between bars keeps them readable as separate
        // intervals at sparkline size instead of merging into a solid block.
        var slot = width / values.Count;
        var barWidth = slot * 0.7;
        var zeroY = scale.ScaleY(0);
        var bars = new StringBuilder();

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v || v == 0)
            {
                continue;
            }

            // A tiny value next to a large one would scale below a pixel and disappear, which
            // would look the same as an interval with no activity. Keep a minimum visible height,
            // growing away from the zero line.
            const double MinBarHeight = 1.5;
            var valueY = scale.ScaleY(v);
            var top = v > 0 ? Math.Min(valueY, zeroY - MinBarHeight) : zeroY;
            var bottom = v > 0 ? zeroY : Math.Max(valueY, zeroY + MinBarHeight);

            var x = (i * slot) + ((slot - barWidth) / 2);
            bars.Append(CultureInfo.InvariantCulture, $"M{x:0.##},{bottom:0.##}V{top:0.##}H{x + barWidth:0.##}V{bottom:0.##}Z");
        }

        return bars.ToString();
    }

    // Sparklines start at zero, like the full chart. A flat 100 then sits at the top and a flat 0
    // on the baseline, rather than both drawing at the same height, and small changes on a large
    // value read as small. Negative values (possible for up-down counters) extend the range below zero.
    private static bool TryCreateScale(IReadOnlyList<double?> values, double height, out Scale scale)
    {
        double? min = null;
        double? max = null;
        foreach (var value in values)
        {
            if (value is { } v)
            {
                min = min is null ? v : Math.Min(min.Value, v);
                max = max is null ? v : Math.Max(max.Value, v);
            }
        }

        if (min is null || max is null)
        {
            scale = default;
            return false;
        }

        scale = new Scale(Math.Min(0, min.Value), Math.Max(0, max.Value), height);
        return true;
    }

    private readonly record struct Scale(double Min, double Max, double Height)
    {
        // An all-zero series has no range; it draws on the baseline.
        public double ScaleY(double value) => Max == Min
            ? Height - Inset
            : Inset + ((Height - (2 * Inset)) * (1 - ((value - Min) / (Max - Min))));
    }

    private static void AppendSegment(StringBuilder line, StringBuilder area, List<(double X, double Y)> points, double zeroY)
    {
        var curve = new StringBuilder();
        curve.Append(CultureInfo.InvariantCulture, $"M{points[0].X:0.##},{points[0].Y:0.##}");

        if (points.Count == 1)
        {
            // A lone point has no length to stroke, so draw a short horizontal tick for it.
            curve.Append(CultureInfo.InvariantCulture, $"h0.5");
        }
        else
        {
            var tangents = CalculateMonotoneTangents(points);
            for (var i = 0; i < points.Count - 1; i++)
            {
                var (x0, y0) = points[i];
                var (x1, y1) = points[i + 1];
                var dx = (x1 - x0) / 3;
                curve.Append(CultureInfo.InvariantCulture,
                    $"C{x0 + dx:0.##},{y0 + (dx * tangents[i]):0.##} {x1 - dx:0.##},{y1 - (dx * tangents[i + 1]):0.##} {x1:0.##},{y1:0.##}");
            }
        }

        line.Append(curve);
        area.Append(curve);
        area.Append(CultureInfo.InvariantCulture, $"L{points[^1].X:0.##},{zeroY:0.##}L{points[0].X:0.##},{zeroY:0.##}Z");
    }

    // Monotone cubic interpolation (Fritsch–Carlson, as used by d3's curveMonotoneX).
    // Unlike Catmull-Rom splines it never overshoots the data, so a flat run of zeros stays
    // flat and a step change doesn't produce a misleading bump above or below the real values.
    // See https://en.wikipedia.org/wiki/Monotone_cubic_interpolation and
    // https://github.com/d3/d3-shape/blob/main/src/curve/monotone.js
    private static double[] CalculateMonotoneTangents(List<(double X, double Y)> points)
    {
        var n = points.Count;
        var slopes = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            slopes[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        }

        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];

        for (var i = 1; i < n - 1; i++)
        {
            var s0 = slopes[i - 1];
            var s1 = slopes[i];
            if (s0 * s1 <= 0)
            {
                // Local extremum or flat section: a zero tangent prevents overshoot.
                tangents[i] = 0;
                continue;
            }

            var h0 = points[i].X - points[i - 1].X;
            var h1 = points[i + 1].X - points[i].X;
            var p = ((s0 * h1) + (s1 * h0)) / (h0 + h1);
            tangents[i] = Math.Sign(s0) * Math.Min(Math.Min(Math.Abs(s0), Math.Abs(s1)), 0.5 * Math.Abs(p)) * 2;
        }

        return tangents;
    }
}
