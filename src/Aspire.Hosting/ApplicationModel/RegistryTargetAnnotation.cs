// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Annotation that indicates a resource should use a specific container registry as its default target.
/// </summary>
/// <remarks>
/// This annotation is automatically added to resources when a container registry is added to the application model.
/// It provides a default registry for resources that don't have an explicit <see cref="ContainerRegistryReferenceAnnotation"/>.
/// </remarks>
/// <param name="registry">The container registry resource.</param>
[Experimental("ASPIRECOMPUTE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RegistryTargetAnnotation(IContainerRegistry registry) : IResourceAnnotation
{
    /// <summary>
    /// Gets the container registry resource.
    /// </summary>
    public IContainerRegistry Registry { get; } = registry;
}
