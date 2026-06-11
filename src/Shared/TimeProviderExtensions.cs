// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Extensions;

internal static class TimeProviderExtensions
{
    public static DateTime ToLocal(this TimeProvider timeProvider, DateTimeOffset utcDateTimeOffset)
    {
        var dateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTimeOffset.UtcDateTime, timeProvider.LocalTimeZone);
        dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Local);

        return dateTime;
    }

    public static DateTimeOffset ToLocalDateTimeOffset(this TimeProvider timeProvider, DateTimeOffset utcDateTimeOffset)
    {
        return TimeZoneInfo.ConvertTime(utcDateTimeOffset, timeProvider.LocalTimeZone);
    }

    public static DateTime ToLocal(this TimeProvider timeProvider, DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Local)
        {
            return dateTime;
        }
        if (dateTime.Kind == DateTimeKind.Unspecified)
        {
            throw new InvalidOperationException("Unable to convert unspecified DateTime to local time.");
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(dateTime, timeProvider.LocalTimeZone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Local);

        return local;
    }
}
