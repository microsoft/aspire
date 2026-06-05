// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Interface for invoking callbacks on a remote client (e.g., TypeScript).
/// This abstraction allows testing the InstructionProcessor without a real JSON-RPC connection.
/// </summary>
internal interface ICallbackInvoker
{
    /// <summary>
    /// Invokes a callback registered on the client side.
    /// </summary>
    /// <typeparam name="TResult">The expected result type.</typeparam>
    /// <param name="callbackId">The callback ID registered on the client.</param>
    /// <param name="args">Arguments to pass to the callback: null, JsonValue (primitive), or JsonObject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result from the callback.</returns>
    Task<TResult> InvokeAsync<TResult>(string callbackId, JsonNode? args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a callback that returns no value.
    /// </summary>
    /// <param name="callbackId">The callback ID registered on the client.</param>
    /// <param name="args">Arguments to pass to the callback: null, JsonValue (primitive), or JsonObject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvokeAsync(string callbackId, JsonNode? args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether a client connection is available.
    /// </summary>
    bool IsConnected { get; }
}
