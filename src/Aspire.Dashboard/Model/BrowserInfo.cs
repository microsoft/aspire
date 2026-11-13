// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public sealed class BrowserInfo
{
    public string? TimeZone { get; set; }
    public List<BrowserTimeZoneShortName>? TimeZoneShortNames { get; set; }
    public string? UserAgent { get; set; }
    public bool Is24HourTime { get; set; }
}

/// <summary>
/// A short name the browser gives its time zone at a UTC offset, for example "PDT" at -420 minutes.
/// </summary>
public sealed class BrowserTimeZoneShortName
{
    public int OffsetMinutes { get; set; }
    public string? Name { get; set; }
}
