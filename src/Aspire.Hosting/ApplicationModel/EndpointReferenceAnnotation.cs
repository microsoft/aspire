// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An annotation that represents a reference from one resource to the endpoints of another resource.
/// </summary>
[DebuggerDisplay(@"Type = {GetType().Name,nq}, Resource = {Resource.Name}, EndpointNames = {UseAllEndpoints ? ""(All)"" : string.Join("", "", EndpointNames)}")]
public sealed class EndpointReferenceAnnotation(IResourceWithEndpoints resource) : IResourceAnnotation
{
    /// <summary>
    /// Gets the resource whose endpoints are being referenced.
    /// </summary>
    public IResourceWithEndpoints Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));

    /// <summary>
    /// Gets or sets a value indicating whether all endpoints on the referenced resource are included.
    /// </summary>
    public bool UseAllEndpoints { get; set; }

    /// <summary>
    /// Gets the set of specific endpoint names that are referenced. When <see cref="UseAllEndpoints"/> is <see langword="true"/>, this set is ignored.
    /// </summary>
    public HashSet<string> EndpointNames { get; } = new(StringComparers.EndpointAnnotationName);

    /// <summary>
    /// Gets or sets the network identifier used as context for resolving endpoint addresses.
    /// </summary>
    public NetworkIdentifier ContextNetworkID { get; set; } = KnownNetworkIdentifiers.LocalhostNetwork;
}
