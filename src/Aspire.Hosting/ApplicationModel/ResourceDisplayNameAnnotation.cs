// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Overrides the human-readable name displayed for a resource.
/// </summary>
/// <param name="resourceDisplayName">The friendly resource name, such as Orders Database.</param>
/// <remarks>
/// This annotation affects presentation only, not resource identity, service discovery, or telemetry identity.
/// </remarks>
public sealed class ResourceDisplayNameAnnotation(string resourceDisplayName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the human-readable resource name.
    /// </summary>
    public string ResourceDisplayName { get; } = !string.IsNullOrWhiteSpace(resourceDisplayName)
        ? resourceDisplayName
        : throw new ArgumentException("The resource display name cannot be empty or whitespace.", nameof(resourceDisplayName));
}
