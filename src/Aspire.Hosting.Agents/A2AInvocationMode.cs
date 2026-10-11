// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Agents;

/// <summary>
/// Specifies how dashboard commands invoke an A2A agent.
/// </summary>
public enum A2AInvocationMode
{
    /// <summary>
    /// Sends a non-streaming A2A message.
    /// </summary>
    NonStreaming,

    /// <summary>
    /// Streams the A2A response when the agent advertises streaming support.
    /// </summary>
    Streaming
}
