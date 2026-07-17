// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.ServiceClient;

internal interface IResourceRepositoryWriter
{
    void ReplaceResources(IReadOnlyList<Resource> resources);
    void ApplyChanges(IReadOnlyList<WatchResourcesChange> changes);
    void MarkConsoleLogsLoaded(string resourceName);
    void AddConsoleLogs(string resourceName, IReadOnlyList<ConsoleLogLine> logLines);
}