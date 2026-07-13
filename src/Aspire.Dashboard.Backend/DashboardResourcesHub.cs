// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardResourcesHub(IDashboardResourceEventSource resourceEventSource) : Hub
{
    public IAsyncEnumerable<DashboardResourcesEvent> WatchResources(CancellationToken cancellationToken)
    {
        return resourceEventSource.WatchAsync(cancellationToken);
    }
}
