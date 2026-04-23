// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal interface IBrowserLogsSessionManager
{
    Task StartSessionAsync(BrowserLogsResource resource, BrowserLogsSettings settings, string resourceName, Uri url, CancellationToken cancellationToken);
}
