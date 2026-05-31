// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agents;

/// <summary>
/// Describes agent-specific metadata for a resource.
/// </summary>
public sealed class AgentResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentResourceAnnotation"/> class.
    /// </summary>
    /// <param name="protocols">The agent protocols supported by the resource.</param>
    /// <param name="customPath">The custom protocol path, when one is configured.</param>
    /// <param name="a2AInvocationMode">The invocation mode used by dashboard commands for A2A protocols.</param>
    public AgentResourceAnnotation(IReadOnlySet<AgentProtocol> protocols, string? customPath, A2AInvocationMode a2AInvocationMode)
    {
        ArgumentNullException.ThrowIfNull(protocols);

        Protocols = protocols;
        CustomPath = customPath;
        A2AInvocationMode = a2AInvocationMode;
    }

    /// <summary>
    /// Gets the agent protocols supported by the resource.
    /// </summary>
    public IReadOnlySet<AgentProtocol> Protocols { get; }

    /// <summary>
    /// Gets the custom protocol path configured for the agent.
    /// </summary>
    public string? CustomPath { get; }

    /// <summary>
    /// Gets the invocation mode used by dashboard commands for A2A protocols.
    /// </summary>
    public A2AInvocationMode A2AInvocationMode { get; }
}
