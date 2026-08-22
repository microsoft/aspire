// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard;
using Aspire.Shared;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};
var app = new DashboardWebApplication(options: options);

using var shutdownCts = new CancellationTokenSource();
var parentWatchdog = ParentProcessWatchdog.Start(shutdownCts);
try
{
    return await app.RunAsync(shutdownCts.Token).ConfigureAwait(false);
}
finally
{
    if (parentWatchdog is not null)
    {
        await parentWatchdog.DisposeAsync().ConfigureAwait(false);
    }
}
