// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Overrides the human-readable type label displayed for a resource.
/// </summary>
/// <remarks>
/// This annotation is internal on purpose: <see cref="Resource.ResourceTypeDisplayName"/> covers the per-type
/// label and <see cref="ResourceBuilderExtensions.WithResourceTypeDisplayName{T}"/> covers the per-instance
/// label, so a public annotation would only add another way to express the same override. The builder
/// extension validates the value before creating this annotation.
/// </remarks>
/// <param name="resourceTypeDisplayName">The type label, such as Redis or PostgreSQL Server.</param>
internal sealed class ResourceTypeDisplayNameAnnotation(string resourceTypeDisplayName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the human-readable resource type label.
    /// </summary>
    public string ResourceTypeDisplayName { get; } = resourceTypeDisplayName;
}
