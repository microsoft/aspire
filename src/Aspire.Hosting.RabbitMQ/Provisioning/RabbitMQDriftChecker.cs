// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Accumulates scalar-field drift comparisons for a single resource probe and returns the first
/// drift found as an unhealthy <see cref="RabbitMQProbeResult"/> (first-drift-wins semantics).
/// </summary>
/// <remarks>
/// Each <c>Field</c> overload appends one comparison. Call <see cref="Result"/> after all fields
/// have been checked; it returns <see cref="RabbitMQProbeResult.Healthy"/> when no drift was found.
/// The generated message format is byte-identical to the inline strings the individual ProbeAsync
/// methods previously produced:
/// <c>{kind} '{name}' {label} drifted: expected '{expected}', found '{found}'.</c>
/// </remarks>
internal sealed class RabbitMQDriftChecker(string kind, string name)
{
    private RabbitMQProbeResult? _firstDrift;

    /// <summary>
    /// Gets the first drift result found, or <see cref="RabbitMQProbeResult.Healthy"/> if no drift was detected.
    /// </summary>
    public RabbitMQProbeResult Result => _firstDrift ?? RabbitMQProbeResult.Healthy;

    /// <summary>
    /// Compares two string values and records drift if they differ.
    /// </summary>
    public RabbitMQDriftChecker Field(string label, string? expected, string? found, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (_firstDrift is null && !string.Equals(expected, found, comparison))
        {
            _firstDrift = RabbitMQProbeResult.Unhealthy($"{kind} '{name}' {label} drifted: expected '{expected}', found '{found}'.");
        }

        return this;
    }

    /// <summary>
    /// Compares two boolean values and records drift if they differ.
    /// </summary>
    public RabbitMQDriftChecker Field(string label, bool expected, bool found)
    {
        if (_firstDrift is null && expected != found)
        {
            _firstDrift = RabbitMQProbeResult.Unhealthy($"{kind} '{name}' {label} drifted: expected '{expected}', found '{found}'.");
        }

        return this;
    }

    /// <summary>
    /// Compares two nullable integer values and records drift if they differ. Null is displayed as
    /// <paramref name="nullDisplay"/> in the drift message (e.g. <c>"(default)"</c> for optional broker fields).
    /// </summary>
    public RabbitMQDriftChecker Field(string label, int? expected, int? found, string nullDisplay = "(null)")
    {
        if (_firstDrift is null && expected != found)
        {
            var expectedStr = expected?.ToString(CultureInfo.InvariantCulture) ?? nullDisplay;
            var foundStr = found?.ToString(CultureInfo.InvariantCulture) ?? nullDisplay;
            _firstDrift = RabbitMQProbeResult.Unhealthy($"{kind} '{name}' {label} drifted: expected '{expectedStr}', found '{foundStr}'.");
        }

        return this;
    }

    /// <summary>
    /// Compares two nullable string values using a custom display string for null (e.g. <c>"(none)"</c>).
    /// Records drift if the values differ (ordinal comparison).
    /// </summary>
    public RabbitMQDriftChecker NullableField(string label, string? expected, string? found, string nullDisplay)
    {
        if (_firstDrift is null && !string.Equals(expected, found, StringComparison.Ordinal))
        {
            _firstDrift = RabbitMQProbeResult.Unhealthy(
                $"{kind} '{name}' {label} drifted: expected '{expected ?? nullDisplay}', found '{found ?? nullDisplay}'.");
        }

        return this;
    }

    /// <summary>
    /// Runs the x-argument bag drift check via <see cref="RabbitMQArgumentDrift"/> and records the
    /// first argument drift if one is found.
    /// </summary>
    public RabbitMQDriftChecker Arguments(string resourceDescription, IDictionary<string, object?>? desired, IDictionary<string, object?>? live)
    {
        if (_firstDrift is null)
        {
            var argDrift = RabbitMQArgumentDrift.Detect(resourceDescription, desired, live);
            if (argDrift is not null)
            {
                _firstDrift = RabbitMQProbeResult.Unhealthy(argDrift);
            }
        }

        return this;
    }
}
