// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines resource-specific behavior that is applied when another resource references the annotated resource.
/// </summary>
/// <remarks>
/// Implement this interface on annotations added to resources that need to augment the standard connection string
/// or service discovery environment produced by <c>WithReference</c>.
/// </remarks>
[Experimental("ASPIREAGENTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IResourceWithReferenceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Determines whether this annotation can apply a reference to the specified source resource.
    /// </summary>
    /// <param name="source">The resource being referenced.</param>
    /// <returns><see langword="true"/> when this annotation can apply the reference; otherwise, <see langword="false"/>.</returns>
    bool CanApplyReference(IResource source);

    /// <summary>
    /// Applies resource-specific reference configuration to the destination resource.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource type.</typeparam>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The resource being referenced.</param>
    /// <param name="referenceName">The name to use for the reference.</param>
    /// <returns>The destination resource builder.</returns>
    IResourceBuilder<TDestination> WithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResource source,
        string referenceName)
        where TDestination : IResourceWithEnvironment;
}
