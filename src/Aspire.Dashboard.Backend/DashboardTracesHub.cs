// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardTracesHub(
    IDashboardTraceSource traceSource,
    DashboardStreamRevocation revocation) : Hub
{
    public async IAsyncEnumerable<DashboardTraceEvent> WatchTraces(
        DashboardTraceStreamRequest streamRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Context.GetHttpContext()?.Request
            ?? throw new HubException("The trace request context is unavailable.");
        var query = new DashboardTraceQuery(
            streamRequest.ResourceNames,
            streamRequest.TraceId,
            streamRequest.HasError,
            Limit: null,
            Search: streamRequest.Search);

        using var linked = revocation.CreateLinkedTokenSource(cancellationToken);

        await foreach (var traceEvent in traceSource.WatchAsync(
            query,
            DashboardRequestCredentials.From(request),
            linked.Token).ConfigureAwait(false))
        {
            yield return traceEvent;
        }
    }
}
