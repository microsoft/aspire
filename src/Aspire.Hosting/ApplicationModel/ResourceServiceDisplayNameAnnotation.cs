// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Overrides the human-readable service label displayed for a resource.
/// </summary>
/// <param name="serviceDisplayName">The service label, such as Redis or Orders API.</param>
/// <remarks>
/// This annotation affects presentation only, not resource identity, service discovery, or telemetry identity.
/// </remarks>
public sealed class ResourceServiceDisplayNameAnnotation(string serviceDisplayName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the human-readable service label.
    /// </summary>
    public string ServiceDisplayName { get; } = !string.IsNullOrWhiteSpace(serviceDisplayName)
        ? serviceDisplayName
        : throw new ArgumentException("The service display name cannot be empty or whitespace.", nameof(serviceDisplayName));
}
