// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Tracks whether consumers are ready to respond to interactions.
/// </summary>
internal sealed class InteractionServiceConsumerState
{
    // The dashboard and CLI backchannel start after before-start hooks complete.
    // The original application build clears readiness, and DistributedApplication restores it after those hooks.
    private volatile bool _ready = true;

    /// <summary>
    /// Gets or sets whether consumers are ready to respond to interactions.
    /// </summary>
    public bool Ready
    {
        get => _ready;
        set => _ready = value;
    }
}
