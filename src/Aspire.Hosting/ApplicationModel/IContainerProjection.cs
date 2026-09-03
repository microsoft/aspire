// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Declares that <typeparamref name="TSelf"/> is the container an instance of <typeparamref name="TOwner"/> can be
/// projected as, and provides the well-known way to create one.
/// </summary>
/// <typeparam name="TOwner">The resource type being projected.</typeparam>
/// <typeparam name="TSelf">The container type implementing this interface.</typeparam>
/// <remarks>
/// <para>
/// Container projections are constructed from their owner so they can adopt the owner's name and annotation
/// collection. Every projection type already followed that convention; declaring it here turns it into a contract the
/// compiler enforces, which is what lets <c>RunAsContainerImage</c> and <c>PublishAsContainerImage</c> create the
/// projection themselves instead of asking each integration to pass a factory that restates the same thing.
/// </para>
/// <para>
/// Resolving the factory through a static abstract member keeps creation statically bound. No reflection runs while
/// the application model is being built, and the pattern stays trim and native AOT safe.
/// </para>
/// <para>
/// Implementing this interface on an existing public type is additive and binary compatible, so projection types that
/// shipped previously can adopt it without breaking integrations compiled against an earlier version.
/// </para>
/// <example>
/// <code language="csharp">
/// public class AzureAppConfigurationEmulatorResource(AzureAppConfigurationResource innerResource)
///     : ContainerResource(innerResource.Name),
///       IContainerProjection&lt;AzureAppConfigurationResource, AzureAppConfigurationEmulatorResource&gt;
/// {
///     public override ResourceAnnotationCollection Annotations =&gt; innerResource.Annotations;
///
///     public static AzureAppConfigurationEmulatorResource CreateProjection(AzureAppConfigurationResource owner)
///         =&gt; new(owner);
/// }
/// </code>
/// </example>
/// </remarks>
public interface IContainerProjection<TOwner, TSelf>
    where TOwner : IResource
    where TSelf : ContainerResource, IContainerProjection<TOwner, TSelf>
{
    /// <summary>
    /// Creates the container <paramref name="owner"/> is projected as.
    /// </summary>
    /// <param name="owner">The resource being projected.</param>
    /// <returns>
    /// A container that uses <paramref name="owner"/>'s name and returns <paramref name="owner"/>'s
    /// <see cref="IResource.Annotations"/> collection.
    /// </returns>
    static abstract TSelf CreateProjection(TOwner owner);
}
