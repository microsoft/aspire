// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Orchestrator;

namespace Aspire.Hosting.Tests.Dcp;

public class ApplicationOrchestratorProxy
{
    internal ApplicationOrchestratorProxy(ApplicationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    private readonly ApplicationOrchestrator _orchestrator;

    public Task StartResourceAsync(string resourceName, CancellationToken cancellationToken) => _orchestrator.StartResourceAsync(resourceName, cancellationToken);

    public Task StopResourceAsync(string resourceName, CancellationToken cancellationToken) => _orchestrator.StopResourceAsync(resourceName, cancellationToken);
}
