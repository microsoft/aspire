// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Hex1b;

namespace Aspire.Hosting.Utils;

/// <summary>
/// An in-memory implementation of <see cref="IInteractionTerminalSessionStore"/> for tests.
/// Records lifecycle calls and never starts a real terminal workload.
/// </summary>
internal sealed class TestInteractionTerminalSessionStore : IInteractionTerminalSessionStore
{
    public ConcurrentQueue<int> StartedInteractions { get; } = new();
    public ConcurrentQueue<IReadOnlyList<(string InputName, Hex1bTerminalBuilder Builder)>> StartedTerminalInputs { get; } = new();
    public ConcurrentQueue<int> CompletedInteractions { get; } = new();
    public ConcurrentQueue<int> CanceledInteractions { get; } = new();

    public void StartInteraction(int interactionId, IReadOnlyList<(string InputName, Hex1bTerminalBuilder Builder)> terminalInputs)
    {
        StartedInteractions.Enqueue(interactionId);
        StartedTerminalInputs.Enqueue(terminalInputs.ToArray());
    }

    public Task AttachAsync(int interactionId, string inputName, Stream clientStream, CancellationToken cancellationToken)
    {
        // Tests that exercise attach do so against the real store; this fake only needs to satisfy the contract.
        return Task.CompletedTask;
    }

    public void CompleteInteraction(int interactionId) => CompletedInteractions.Enqueue(interactionId);

    public void CancelInteraction(int interactionId) => CanceledInteractions.Enqueue(interactionId);
}
