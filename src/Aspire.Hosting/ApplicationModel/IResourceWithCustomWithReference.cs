// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines custom <c>WithReference</c> dispatch behavior for a resource type.
/// </summary>
/// <typeparam name="TSelf">The concrete resource type that provides the custom dispatch behavior.</typeparam>
/// <remarks>
/// This contract is used by the internal ATS-visible <c>withReference</c> dispatcher
/// to route references to resource-specific logic at runtime. Implementations may
/// customize dispatch based on either the source or destination resource type.
/// </remarks>
[Experimental("ASPIREATS001")]
public interface IResourceWithCustomWithReference<TSelf> : IResource
    where TSelf : IResource, IResourceWithCustomWithReference<TSelf>
{
    /// <summary>
    /// Applies a reference from <paramref name="source"/> to <paramref name="builder"/> using resource-specific behavior.
    /// </summary>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="source">The source resource builder.</param>
    /// <param name="connectionName">An optional connection string override used by connection-string-based references.</param>
    /// <param name="optional"><see langword="true"/> to allow a missing connection string; otherwise, <see langword="false"/>.</param>
    /// <param name="name">An optional service discovery name override used by service-based references.</param>
    /// <typeparam name="TDestination">The destination resource type.</typeparam>
    /// <returns>The destination <see cref="IResourceBuilder{T}"/> when handled; otherwise, <see langword="null"/>.</returns>
    static abstract IResourceBuilder<TDestination>? TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TDestination : IResourceWithEnvironment;
}
