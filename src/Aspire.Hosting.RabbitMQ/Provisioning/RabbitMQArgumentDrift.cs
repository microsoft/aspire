// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Best-effort drift comparison for the declared x-argument / definition bags that Aspire authors.
/// </summary>
/// <remarks>
/// <para>
/// Comparison is scoped to exactly the keys Aspire declared (the desired bag). We do not flag extra keys
/// present on the live side, because the broker and other tools may add server-managed keys that Aspire
/// did not author — asserting on those would replicate server semantics, which is out of scope.
/// </para>
/// <para>
/// Values read back from the management API arrive as <see cref="JsonElement"/> (the bag deserializes to
/// <c>IDictionary&lt;string, object?&gt;</c>). Comparison is best-effort: a value that cannot be compared
/// losslessly against the declared value (because the broker normalized it, or the JSON kind does not map
/// cleanly onto the declared CLR type) is skipped rather than reported as false drift.
/// </para>
/// </remarks>
internal static class RabbitMQArgumentDrift
{
    /// <summary>
    /// Compares the <paramref name="desired"/> declared bag against the <paramref name="live"/> bag read
    /// back from the broker. Returns a human-readable drift description for the first drifted key, or
    /// <see langword="null"/> when no drift is detected (including when both bags are empty).
    /// </summary>
    public static string? Detect(string resourceDescription, IDictionary<string, object?>? desired, IDictionary<string, object?>? live)
    {
        if (desired is null || desired.Count == 0)
        {
            return null;
        }

        live ??= new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, desiredValue) in desired)
        {
            if (!live.TryGetValue(key, out var liveValue))
            {
                return $"{resourceDescription}: declared argument '{key}' is missing on the broker.";
            }

            // Best-effort: only report drift when we can confidently compare the two values. If we cannot
            // (unknown/normalized representation), skip rather than raise a false positive.
            if (TryCompare(desiredValue, liveValue, out var equal) && !equal)
            {
                return $"{resourceDescription}: argument '{key}' drifted (expected '{Describe(desiredValue)}', found '{Describe(liveValue)}').";
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts a lossless comparison between a declared CLR value and a live value (typically a
    /// <see cref="JsonElement"/>). Sets <paramref name="equal"/> and returns <see langword="true"/> only when
    /// a confident comparison was possible; returns <see langword="false"/> to signal "skip this field".
    /// </summary>
    private static bool TryCompare(object? desired, object? live, out bool equal)
    {
        equal = false;

        if (desired is null || live is null)
        {
            // A null on either side is ambiguous (could be broker normalization); skip unless both null.
            return desired is null && live is null && (equal = true);
        }

        // Unwrap the live JSON value into a primitive we can compare against the declared CLR value.
        if (live is JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when TryToInt64(desired, out var desiredNumber) && element.TryGetInt64(out var liveNumber):
                    equal = desiredNumber == liveNumber;
                    return true;
                case JsonValueKind.String when desired is string desiredString:
                    equal = string.Equals(desiredString, element.GetString(), StringComparison.Ordinal);
                    return true;
                case JsonValueKind.True or JsonValueKind.False when desired is bool desiredBool:
                    equal = desiredBool == element.GetBoolean();
                    return true;
                default:
                    // Objects, arrays, mismatched kinds, or values that don't round-trip cleanly: skip.
                    return false;
            }
        }

        // Both are plain CLR values (e.g. in unit tests that populate the live bag directly).
        if (TryToInt64(desired, out var d) && TryToInt64(live, out var l))
        {
            equal = d == l;
            return true;
        }

        if (desired is string ds && live is string ls)
        {
            equal = string.Equals(ds, ls, StringComparison.Ordinal);
            return true;
        }

        if (desired is bool db && live is bool lb)
        {
            equal = db == lb;
            return true;
        }

        // Fall back to Equals only for identical runtime types; otherwise skip to avoid false drift.
        if (desired.GetType() == live.GetType())
        {
            equal = desired.Equals(live);
            return true;
        }

        return false;
    }

    private static bool TryToInt64(object value, out long result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "(null)",
        JsonElement element => element.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(null)"
    };
}
