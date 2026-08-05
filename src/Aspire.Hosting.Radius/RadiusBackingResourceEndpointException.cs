// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Thrown when the address of a Radius <em>backing</em> resource — a cache, database, or queue
/// provisioned by a Radius recipe rather than deployed as a <c>Radius.Compute/containers</c>
/// workload — cannot be determined.
/// </summary>
/// <remarks>
/// <para>
/// A backing resource's Kubernetes objects and credentials are created by its recipe, so no value
/// derived from the Aspire endpoint model describes it. The Radius publisher projects the address
/// out of the recipe's own outputs instead. Every other route to that address — including
/// <see cref="RadiusEnvironmentResource.GetHostAddressExpression(EndpointReference)"/> and the
/// cross-environment endpoint resolution other compute publishers use — would have to guess, so it
/// throws this exception rather than emitting an address that silently resolves to nothing. See
/// <see href="https://github.com/microsoft/aspire/issues/18935"/>.
/// </para>
/// <para>
/// This type is public because it can surface from a Kubernetes, Azure Container Apps, or Azure
/// App Service publish: those publishers resolve a cross-environment reference through the Radius
/// environment that owns the resource. Catching it requires a type an AppHost author can name.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/> to match the package's publish-time
/// failure convention. Note that
/// <c>AzureAppServiceEnvironmentResource</c>'s validation pass catches
/// <see cref="InvalidOperationException"/> around its context lookup; that catch does not cover
/// environment-variable resolution, so this exception is not swallowed there.
/// </para>
/// </remarks>
/// <example>
/// Deploying the consumer and the backing resource to the same Radius environment resolves it.
/// An AppHost that publishes several environments can report the offending resource:
/// <code language="csharp">
/// try
/// {
///     await app.RunAsync();
/// }
/// catch (RadiusBackingResourceEndpointException ex)
/// {
///     Console.Error.WriteLine($"'{ex.Resource.Name}' is provisioned by a Radius recipe in " +
///                             "another environment, so its address cannot be resolved here.");
/// }
/// </code>
/// </example>
public sealed class RadiusBackingResourceEndpointException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RadiusBackingResourceEndpointException"/> class.
    /// </summary>
    /// <param name="resource">The backing resource whose address could not be determined.</param>
    /// <param name="message">The message that describes the error.</param>
    public RadiusBackingResourceEndpointException(IResource resource, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(resource);

        Resource = resource;
    }

    /// <summary>
    /// Gets the backing resource whose address could not be determined.
    /// </summary>
    public IResource Resource { get; }
}
