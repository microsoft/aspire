// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

internal static class DcpVersion
{
    public static Version MinimumVersionInclusive = new Version(0, 23, 3); // Aspire 13.3

    /// <summary>
    /// Development build version proxy, considered always "current" and supporting latest features. 
    /// </summary>
    public static Version Dev = new Version(1000, 0, 0);
}
