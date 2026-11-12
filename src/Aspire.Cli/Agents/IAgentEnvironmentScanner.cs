// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Interface for scanning and detecting agent environments.
/// Each scanner returns runtime evidence for one or more environments.
/// </summary>
internal interface IAgentEnvironmentScanner
{
    /// <summary>
    /// Scans for agent environments.
    /// </summary>
    /// <param name="context">The directories to scan.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<AgentEnvironment> ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken);
}
