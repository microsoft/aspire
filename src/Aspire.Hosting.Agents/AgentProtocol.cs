// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Agents;

/// <summary>
/// Specifies the protocols supported by an agent resource.
/// </summary>
public enum AgentProtocol
{
    /// <summary>
    /// The Agent2Agent protocol.
    /// </summary>
    A2A,

    /// <summary>
    /// The OpenAI Responses API protocol.
    /// </summary>
    Responses,

    /// <summary>
    /// The AG-UI protocol.
    /// </summary>
    AgUi,

    /// <summary>
    /// The Agent Communication Protocol.
    /// </summary>
    Acp
}
