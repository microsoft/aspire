// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Dashboard.Otlp.Model;

/// <summary>
/// Shared helper methods for working with OTLP data.
/// Used by both Dashboard and CLI.
/// </summary>
public static partial class OtlpHelpers
{
    /// <summary>
    /// The attribute name for Aspire's log entry ID.
    /// </summary>
    public const string AspireLogIdAttribute = "aspire.log_id";

    /// <summary>
    /// The attribute name for the resolved destination name of a span.
    /// </summary>
    public const string AspireDestinationNameAttribute = "aspire.destination";

    /// <summary>
    /// The standard length for shortened trace/span IDs.
    /// </summary>
    public const int ShortenedIdLength = 7;

    /// <summary>
    /// Shortens a trace or span ID to the standard display length.
    /// </summary>
    public static string ToShortenedId(string id) => TruncateString(id, maxLength: ShortenedIdLength);

    /// <summary>
    /// Truncates a string to the specified maximum length.
    /// </summary>
    public static string TruncateString(string value, int maxLength)
    {
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    /// <summary>
    /// Converts Unix nanoseconds to a DateTime (UTC).
    /// </summary>
    public static DateTime UnixNanoSecondsToDateTime(ulong unixTimeNanoseconds)
    {
        var ticks = NanosecondsToTicks(unixTimeNanoseconds);
        return DateTime.UnixEpoch.AddTicks(ticks);
    }

    /// <summary>
    /// Converts a DateTime to Unix nanoseconds.
    /// </summary>
    public static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var timeSinceEpoch = dateTime.ToUniversalTime() - DateTime.UnixEpoch;
        return (ulong)timeSinceEpoch.Ticks * TimeSpan.NanosecondsPerTick;
    }

    /// <summary>
    /// Converts nanoseconds to ticks.
    /// </summary>
    public static long NanosecondsToTicks(ulong nanoseconds)
    {
        return (long)(nanoseconds / TimeSpan.NanosecondsPerTick);
    }

    /// <summary>
    /// Converts nanoseconds to a TimeSpan.
    /// </summary>
    public static TimeSpan NanosecondsToTimeSpan(ulong nanoseconds)
    {
        return TimeSpan.FromTicks(NanosecondsToTicks(nanoseconds));
    }

    /// <summary>
    /// Calculates duration as a TimeSpan from start and end nanosecond timestamps.
    /// </summary>
    public static TimeSpan CalculateDuration(ulong? startNano, ulong? endNano)
    {
        if (startNano.HasValue && endNano.HasValue && endNano.Value >= startNano.Value)
        {
            return NanosecondsToTimeSpan(endNano.Value - startNano.Value);
        }
        return TimeSpan.Zero;
    }

    public static string GetResourceName(IOtlpResource resource, IReadOnlyList<IOtlpResource> allResources)
    {
        var count = 0;
        foreach (var item in allResources)
        {
            if (string.Equals(item.ResourceName, resource.ResourceName, StringComparisons.ResourceName))
            {
                count++;
                if (count >= 2)
                {
                    var instanceId = resource.InstanceId;

                    // Convert long GUID into a shorter, more human friendly format.
                    // Before: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
                    // After:  aaaaaaaa
                    if (instanceId != null && Guid.TryParse(instanceId, out var guid))
                    {
                        Span<char> chars = stackalloc char[32];
                        var result = guid.TryFormat(chars, charsWritten: out _, format: "N");
                        Debug.Assert(result, "Guid.TryFormat not successful.");

                        instanceId = chars.Slice(0, 8).ToString();
                    }

                    if (instanceId == null)
                    {
                        return item.ResourceName;
                    }

                    return $"{item.ResourceName}-{instanceId}";
                }
            }
        }

        return resource.ResourceName;
    }
}
