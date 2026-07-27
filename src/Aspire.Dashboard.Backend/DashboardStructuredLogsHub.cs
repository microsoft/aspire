// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardStructuredLogsHub(IDashboardStructuredLogSource structuredLogSource) : Hub
{
    public async IAsyncEnumerable<DashboardStructuredLogsEvent> WatchStructuredLogs(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Context.GetHttpContext()?.Request
            ?? throw new HubException("The structured-log request context is unavailable.");

        await foreach (var logEvent in structuredLogSource.WatchAsync(
            DashboardRequestCredentials.From(request),
            cancellationToken).ConfigureAwait(false))
        {
            yield return logEvent;
        }
    }
}
