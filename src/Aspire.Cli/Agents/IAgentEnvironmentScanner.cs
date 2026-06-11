// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Interface for scanning and detecting agent environments.
/// Each scanner can detect one or more agent environments and add applicators to the context.
/// </summary>
internal interface IAgentEnvironmentScanner
{
    /// <summary>
    /// Scans for agent environments and adds any detected applicators to the context.
    /// </summary>
    /// <param name="context">The scan context to add detected applicators to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken);
}
