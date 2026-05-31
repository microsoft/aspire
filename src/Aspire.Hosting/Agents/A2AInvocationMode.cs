// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Agents;

/// <summary>
/// Specifies how dashboard commands invoke A2A agents.
/// </summary>
public enum A2AInvocationMode
{
    /// <summary>
    /// Invoke the agent with the non-streaming A2A send message operation.
    /// </summary>
    NonStreaming,

    /// <summary>
    /// Invoke the agent with the streaming A2A send message operation.
    /// </summary>
    Streaming
}
