// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Protocols supported by Microsoft Foundry hosted agent containers.
/// </summary>
public enum HostedAgentProtocol
{
    /// <summary>
    /// The hosted agent container exposes the Responses protocol.
    /// </summary>
    Responses,

    /// <summary>
    /// The hosted agent container exposes the Invocations protocol.
    /// </summary>
    Invocations
}
