// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    public bool WasScanned { get; private set; }
    public AgentEnvironmentScanContext? ScanContext { get; private set; }
    public AgentEnvironment DetectedEnvironments { get; init; }

    public Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasScanned = true;
        ScanContext = context;

        return Task.FromResult(DetectedEnvironments);
    }
}
