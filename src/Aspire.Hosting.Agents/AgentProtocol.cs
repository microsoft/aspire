// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Agents;

/// <summary>
/// Specifies the protocols supported by an agent resource.
/// </summary>
public enum AgentProtocol
{
    /// <summary>
    /// The Agent2Agent protocol using JSON-RPC 2.0 over HTTP.
    /// </summary>
    A2AJsonRpc,

    /// <summary>
    /// The Agent2Agent protocol using gRPC.
    /// </summary>
    A2AGrpc,

    /// <summary>
    /// The Agent2Agent protocol using HTTP with JSON payloads.
    /// </summary>
    A2AHttpJson,

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
