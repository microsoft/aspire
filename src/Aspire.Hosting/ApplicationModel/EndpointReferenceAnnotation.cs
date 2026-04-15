// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

[DebuggerDisplay(@"Type = {GetType().Name,nq}, Resource = {Resource.Name}, EndpointNames = {UseAllEndpoints ? ""(All)"" : string.Join("", "", EndpointNames)}")]
internal sealed class EndpointReferenceAnnotation(IResourceWithEndpoints resource) : IResourceAnnotation
{
    public IResourceWithEndpoints Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));
    public bool UseAllEndpoints { get; set; }
    public HashSet<string> EndpointNames { get; } = new(StringComparers.EndpointAnnotationName);

    public NetworkIdentifier ContextNetworkID { get; set; } = KnownNetworkIdentifiers.LocalhostNetwork;
}
