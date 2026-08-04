// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for creating updated <see cref="CustomResourceSnapshot"/> values.
/// </summary>
public static class CustomResourceSnapshotExtensions
{
    /// <summary>
    /// Creates a copy of the resource snapshot with the specified health reports.
    /// </summary>
    /// <param name="snapshot">The resource snapshot to update.</param>
    /// <param name="healthReports">The health reports to publish for the resource snapshot.</param>
    /// <returns>A copy of <paramref name="snapshot"/> with updated health reports.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This method is intended for use with <see cref="ResourceNotificationService.PublishUpdateAsync(IResource, Func{CustomResourceSnapshot, CustomResourceSnapshot})"/>
    /// and <see cref="ResourceNotificationService.PublishUpdateAsync(IResource, string, Func{CustomResourceSnapshot, CustomResourceSnapshot})"/>.
    /// Updating health reports also recomputes <see cref="CustomResourceSnapshot.HealthStatus"/> based on the snapshot state.
    /// </remarks>
    public static CustomResourceSnapshot WithHealthReports(this CustomResourceSnapshot snapshot, ImmutableArray<HealthReportSnapshot> healthReports)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot with { HealthReports = healthReports };
    }
}
