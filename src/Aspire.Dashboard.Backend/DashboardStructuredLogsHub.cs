// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardStructuredLogsHub(
    IDashboardStructuredLogSource structuredLogSource,
    DashboardStreamRevocation revocation) : Hub
{
    public async IAsyncEnumerable<DashboardStructuredLogsEvent> WatchStructuredLogs(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Context.GetHttpContext()?.Request
            ?? throw new HubException("The structured-log request context is unavailable.");

        using var linked = revocation.CreateLinkedTokenSource(cancellationToken);

        await foreach (var logEvent in structuredLogSource.WatchAsync(
            DashboardRequestCredentials.From(request),
            linked.Token).ConfigureAwait(false))
        {
            yield return logEvent;
        }
    }
}
