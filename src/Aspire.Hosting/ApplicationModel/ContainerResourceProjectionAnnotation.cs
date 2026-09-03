// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores the container configuration view applied to a resource for the current operation.
/// </summary>
internal sealed class ContainerResourceProjectionAnnotation : IResourceAnnotation
{
    private readonly List<Action> _pendingConfiguration = [];
    private bool _configuring;

    internal ContainerResourceProjectionAnnotation(IResource owner, ContainerResource projection)
    {
        Owner = owner;
        Projection = projection;
    }

    /// <summary>
    /// Gets the canonical model resource the projection was created for.
    /// </summary>
    /// <remarks>
    /// A projection shares the owner's <see cref="ResourceAnnotationCollection"/>, so this annotation is
    /// reachable from both sides of the pair. Storing the owner here lets projection types authored by
    /// integrations (for example the Azure emulator surrogates) participate without implementing a marker
    /// interface, which keeps the projection contract additive for resources compiled against earlier versions.
    /// </remarks>
    internal IResource Owner { get; }

    internal ContainerResource Projection { get; }

    /// <summary>
    /// Queues configuration for the projection and applies anything not yet applied.
    /// </summary>
    /// <remarks>
    /// Configuration is routed through the annotation rather than invoked by the caller so the projection owns
    /// when callbacks run. Queuing before draining is what lets a callback project the same resource again
    /// without the nested configuration running ahead of the configuration that is already in flight.
    /// </remarks>
    internal void Configure(Action configure)
    {
        _pendingConfiguration.Add(configure);
        EnsureConfigured();
    }

    private void EnsureConfigured()
    {
        // A configuration callback can project the same resource again (for example an integration whose
        // RunAsContainer defaults call another RunAsContainer overload). Guarding reentrancy keeps the nested
        // registration from draining the queue mid-flight, and draining by index lets configuration queued during
        // a callback still run, in declaration order, once control returns to the outermost drain.
        if (_configuring)
        {
            return;
        }

        _configuring = true;
        try
        {
            for (var i = 0; i < _pendingConfiguration.Count; i++)
            {
                _pendingConfiguration[i]();
            }

            _pendingConfiguration.Clear();
        }
        finally
        {
            _configuring = false;
        }
    }
}
