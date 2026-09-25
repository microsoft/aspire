// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Dashboard;

/// <summary>
/// Provides the shared activity source for dashboard operations.
/// </summary>
public sealed class DashboardActivitySource : IDisposable
{
    /// <summary>
    /// The name of the dashboard activity source.
    /// </summary>
    public const string ActivitySourceName = "Aspire.Dashboard";

    /// <summary>
    /// Gets the shared activity source for dashboard operations.
    /// </summary>
    public ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    /// <inheritdoc />
    public void Dispose() => ActivitySource.Dispose();
}