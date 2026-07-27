// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace Aspire.Dashboard.Backend;

internal sealed class DashboardConsoleLogsHub(IDashboardConsoleLogSource consoleLogSource) : Hub
{
    public async IAsyncEnumerable<DashboardConsoleLogsEvent> WatchConsoleLogs(
        string resourceName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new HubException("A resource name is required.");
        }

        var request = Context.GetHttpContext()?.Request
            ?? throw new HubException("The console-log request context is unavailable.");

        await foreach (var logEvent in consoleLogSource.WatchAsync(
            resourceName,
            DashboardRequestCredentials.From(request),
            cancellationToken).ConfigureAwait(false))
        {
            yield return logEvent;
        }
    }
}
