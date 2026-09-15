// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agents;

/// <summary>
/// Describes agent-specific metadata for a resource.
/// </summary>
/// <remarks>
/// A resource can have multiple <see cref="AgentResourceAnnotation"/> instances when it exposes multiple agent protocols.
/// Each annotation describes one protocol and its path configuration.
/// </remarks>
public sealed class AgentResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentResourceAnnotation"/> class.
    /// </summary>
    /// <param name="protocol">The agent protocol supported by the resource.</param>
    /// <param name="customPath">The custom protocol path, when one is configured.</param>
    /// <param name="invocationMode">The invocation mode used by dashboard commands.</param>
    /// <param name="agentName">The protocol-specific registered agent name, when configured.</param>
    public AgentResourceAnnotation(
        AgentProtocol protocol,
        string? customPath,
        A2AInvocationMode invocationMode = A2AInvocationMode.NonStreaming,
        string? agentName = null)
    {
        Protocol = protocol;
        CustomPath = customPath;
        InvocationMode = invocationMode;
        AgentName = agentName;
    }

    /// <summary>
    /// Gets the agent protocol supported by the resource.
    /// </summary>
    public AgentProtocol Protocol { get; }

    /// <summary>
    /// Gets the custom protocol path configured for the agent.
    /// </summary>
    public string? CustomPath { get; }

    /// <summary>
    /// Gets the invocation mode used by dashboard commands.
    /// </summary>
    public A2AInvocationMode InvocationMode { get; }

    /// <summary>
    /// Gets the protocol-specific registered agent name used by dashboard commands.
    /// </summary>
    public string? AgentName { get; }
}
