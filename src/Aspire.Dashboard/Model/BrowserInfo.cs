// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public sealed class BrowserInfo
{
    public string? TimeZone { get; set; }
    public string? UserAgent { get; set; }
    public bool Is24HourTime { get; set; }
}
