// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardResourcesHub(
    IDashboardResourceEventSource resourceEventSource,
    DashboardStreamRevocation revocation) : Hub
{
    public async IAsyncEnumerable<DashboardResourcesEvent> WatchResources(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = revocation.CreateLinkedTokenSource(cancellationToken);

        await foreach (var resourceEvent in resourceEventSource.WatchAsync(linked.Token).ConfigureAwait(false))
        {
            yield return resourceEvent;
        }
    }
}
