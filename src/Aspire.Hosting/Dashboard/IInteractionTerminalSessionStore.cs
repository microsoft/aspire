// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

namespace Aspire.Hosting;

/// <summary>
/// Tracks the AppHost-owned terminal sessions belonging to <see cref="InputType.Terminal"/> interaction inputs.
/// </summary>
/// <remarks>
/// This mirrors <see cref="IInteractionFileUploadStore"/>: the interaction itself only carries a handle over the
/// dashboard gRPC channel, while the payload — here a live HMP1 byte stream rather than file bytes — is moved over a
/// dedicated streaming RPC.
/// </remarks>
internal interface IInteractionTerminalSessionStore
{
    /// <summary>
    /// Registers an interaction and the terminal inputs that can be attached to.
    /// </summary>
    void StartInteraction(int interactionId, IReadOnlyList<(string InputName, Hex1bTerminalBuilder Builder)> terminalInputs);

    /// <summary>
    /// Attaches a client to a terminal session, starting the session if this is the first client.
    /// </summary>
    /// <returns>
    /// A task that completes when the session ends or <paramref name="cancellationToken"/> is signalled. Callers keep
    /// their transport open until it completes.
    /// </returns>
    Task AttachAsync(int interactionId, string inputName, Stream clientStream, CancellationToken cancellationToken);

    /// <summary>
    /// Tears down every terminal session owned by an interaction that completed normally.
    /// </summary>
    void CompleteInteraction(int interactionId);

    /// <summary>
    /// Tears down every terminal session owned by an interaction that was cancelled.
    /// </summary>
    void CancelInteraction(int interactionId);
}
