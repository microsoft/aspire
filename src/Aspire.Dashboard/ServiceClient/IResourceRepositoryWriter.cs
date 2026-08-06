// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

internal interface IResourceRepositoryWriter
{
    Task ReplaceResourcesAsync(IReadOnlyList<Resource> resources);
    Task ApplyChangesAsync(IReadOnlyList<WatchResourcesChange> changes);
    Task MarkConsoleLogsLoadedAsync(string resourceName);
    Task AddConsoleLogsAsync(string resourceName, IReadOnlyList<ConsoleLogLine> logLines);
}