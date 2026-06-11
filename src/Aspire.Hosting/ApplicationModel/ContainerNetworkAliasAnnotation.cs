// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation for a custom network alias for a container resource.
/// </summary>
/// <remarks>
/// Network aliases enable DNS resolution of the container on the network by custom names.
/// Multiple aliases can be specified for a single container by adding multiple annotations.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Alias = {Alias}, Network = {Network}")]
public sealed class ContainerNetworkAliasAnnotation : IResourceAnnotation
{
    private readonly string _alias;
    private NetworkIdentifier _network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork;

    /// <summary>
    /// Creates a new instance of the <see cref="ContainerNetworkAliasAnnotation"/> class with the specified alias.
    /// </summary>
    public ContainerNetworkAliasAnnotation(string alias)
    {
        ArgumentOutOfRangeException.ThrowIfNullOrWhiteSpace(alias, nameof(alias));
        _alias = alias;
    }

    /// <summary>
    /// Gets the network alias for the container.
    /// </summary>
    public string Alias => _alias;

    /// <summary>
    /// Gets or sets the network identifier for the network to which the alias applies.
    /// </summary>
    /// <remarks>
    /// If not specified, defaults to the default Aspire container network.
    /// </remarks>
    public NetworkIdentifier Network
    {
        get => _network;
        set
        {
            ArgumentNullException.ThrowIfNull(value, nameof(value));
            ArgumentOutOfRangeException.ThrowIfNullOrWhiteSpace(value.Value, nameof(value));
            _network = value;
        }
    }
}
