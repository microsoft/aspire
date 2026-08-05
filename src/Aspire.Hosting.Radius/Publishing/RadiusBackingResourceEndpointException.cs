// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Thrown when an Aspire endpoint of a Radius <em>backing</em> resource (a cache, database, or
/// queue provisioned by a Radius recipe) is resolved through the compute-environment endpoint
/// helpers, which can only address <c>Radius.Compute/containers</c> workloads.
/// </summary>
/// <remarks>
/// This is a dedicated type — rather than a bare <see cref="InvalidOperationException"/> — because
/// <c>RadiusInfrastructureBuilder.ResolveEnvironmentAsync</c> deliberately swallows
/// <see cref="InvalidOperationException"/> for endpoints that cannot be resolved at publish time.
/// Letting this case be swallowed would silently drop a variable the consumer requires, which is
/// exactly the class of failure <see href="https://github.com/microsoft/aspire/issues/18935"/>
/// describes.
/// </remarks>
internal sealed class RadiusBackingResourceEndpointException : Exception
{
    public RadiusBackingResourceEndpointException(IResource resource, string message)
        : base(message)
    {
        Resource = resource;
    }

    /// <summary>Gets the backing resource whose endpoint could not be addressed.</summary>
    public IResource Resource { get; }
}
