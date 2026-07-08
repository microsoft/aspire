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
    public AgentResourceAnnotation(AgentProtocol protocol, string? customPath)
    {
        Protocol = protocol;
        CustomPath = customPath;
    }

    /// <summary>
    /// Gets the agent protocol supported by the resource.
    /// </summary>
    public AgentProtocol Protocol { get; }

    /// <summary>
    /// Gets the custom protocol path configured for the agent.
    /// </summary>
    public string? CustomPath { get; }

}
