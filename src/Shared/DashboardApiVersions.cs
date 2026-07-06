// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Dashboard API version constants. The AppHost reports the current version to
/// the dashboard so it can detect when it is too old to support the AppHost's features.
/// </summary>
internal static class DashboardApiVersions
{
    // Version1: Aspire 13.5
    // Adds file upload support in interactions.
    internal const int Version1 = 1;

    /// <summary>
    /// The current API version supported by this build.
    /// </summary>
    internal const int Current = Version1;
}
