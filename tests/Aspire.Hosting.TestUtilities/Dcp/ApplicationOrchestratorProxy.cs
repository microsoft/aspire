// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Orchestrator;

namespace Aspire.Hosting.Tests.Dcp;

public class ApplicationOrchestratorProxy
{
    internal ApplicationOrchestratorProxy(ApplicationOrchestrator orchestrator, DcpExecutorEvents dcpExecutorEvents)
    {
        _orchestrator = orchestrator;
        _dcpExecutorEvents = dcpExecutorEvents;
    }

    private readonly ApplicationOrchestrator _orchestrator;
    private readonly DcpExecutorEvents _dcpExecutorEvents;

    public Task PublishResourcesPreparedAsync(CancellationToken cancellationToken) =>
        _dcpExecutorEvents.PublishAsync(new OnResourcesPreparedContext(cancellationToken));

    public Task PublishResourceStartingAsync(ContainerResource resource, CancellationToken cancellationToken) =>
        _dcpExecutorEvents.PublishAsync(new OnResourceStartingContext(
            cancellationToken, KnownResourceTypes.Container, resource, resource.Name));

    public Task StartResourceAsync(string resourceName, CancellationToken cancellationToken) => _orchestrator.StartResourceAsync(resourceName, cancellationToken);

    public Task StopResourceAsync(string resourceName, CancellationToken cancellationToken) => _orchestrator.StopResourceAsync(resourceName, cancellationToken);
}
