// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Dashboard;

namespace Aspire.Hosting.Tests.Helpers;

internal static class DashboardServiceDataExtensions
{
    public static async Task<ResourceSnapshot> WaitForResourceAsync(this DashboardServiceData dashboardServiceData, string resourceName, Func<ResourceSnapshot, bool> predicate, CancellationToken cancellationToken = default)
    {
        var (initialData, updates) = dashboardServiceData.SubscribeResources();
        if (TryFindMatch(initialData, resourceName, predicate, out var match))
        {
            return match;
        }

        await foreach (var changes in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (TryFindMatch(changes.Where(c => c.ChangeType != ResourceSnapshotChangeType.Delete).Select(c => c.Resource), resourceName, predicate, out match))
            {
                return match;
            }
        }

        throw new OperationCanceledException($"The operation was cancelled before the resource met the predicate condition.");
    }

    private static bool TryFindMatch(IEnumerable<ResourceSnapshot> resources, string resourceName, Func<ResourceSnapshot, bool> predicate, [NotNullWhen(true)] out ResourceSnapshot? match)
    {
        foreach (var resource in resources)
        {
            if (string.Equals(resourceName, resource.Name, StringComparisons.ResourceName)
                && predicate(resource))
            {
                match = resource;
                return true;
            }
        }

        match = null;
        return false;
    }
}
