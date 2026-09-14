// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Displays an indeterminate or value-based circular progress indicator.
/// </summary>
public partial class AspireProgressRing
{
    private const double CircleCircumference = 2 * Math.PI * 7;

    /// <summary>
    /// Gets or sets the accessible name of the progress indicator.
    /// </summary>
    [Parameter, EditorRequired]
    public required string AriaLabel { get; set; }

    /// <summary>
    /// Gets or sets the minimum value of the progress range.
    /// </summary>
    [Parameter]
    public double Min { get; set; }

    /// <summary>
    /// Gets or sets the maximum value of the progress range.
    /// </summary>
    [Parameter]
    public double Max { get; set; } = 100;

    /// <summary>
    /// Gets or sets the current value. When omitted, the progress ring is indeterminate.
    /// </summary>
    [Parameter]
    public double? Value { get; set; }

    /// <summary>
    /// Gets or sets the width and height of the progress ring.
    /// </summary>
    [Parameter]
    public string? Width { get; set; }

    /// <summary>
    /// Gets or sets additional CSS classes applied to the progress ring.
    /// </summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>
    /// Gets or sets additional inline styles applied to the progress ring.
    /// </summary>
    [Parameter]
    public string? Style { get; set; }

    /// <summary>
    /// Gets or sets additional attributes applied to the progress ring.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    private double EffectiveValue => Max > Min ? Math.Clamp(Value ?? Min, Min, Max) : Min;

    private double Progress => Max > Min ? (EffectiveValue - Min) / (Max - Min) : 0;

    private string CssClass => $"aspire-progress-ring {(Value.HasValue ? "determinate" : "indeterminate")} {Class}";

    private string CssStyle
    {
        get
        {
            var sizeStyle = Width is null ? null : $"--aspire-progress-ring-size: {Width}; ";

            if (!Value.HasValue)
            {
                return $"{sizeStyle}{Style}";
            }

            var dashOffset = CircleCircumference * (1 - Progress);
            return string.Create(CultureInfo.InvariantCulture, $"{sizeStyle}--aspire-progress-ring-dash-offset: {dashOffset}; {Style}");
        }
    }
}
