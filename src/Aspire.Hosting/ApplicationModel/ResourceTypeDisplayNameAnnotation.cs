// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Overrides the human-readable type label displayed for a resource.
/// </summary>
/// <param name="resourceTypeDisplayName">The type label, such as Redis or PostgreSQL Server.</param>
/// <remarks>
/// This annotation affects presentation only, not resource identity, service discovery, or telemetry identity.
/// </remarks>
public sealed class ResourceTypeDisplayNameAnnotation(string resourceTypeDisplayName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the human-readable resource type label.
    /// </summary>
    public string ResourceTypeDisplayName { get; } = !string.IsNullOrWhiteSpace(resourceTypeDisplayName)
        ? resourceTypeDisplayName
        : throw new ArgumentException("The resource type display name cannot be empty or whitespace.", nameof(resourceTypeDisplayName));
}
