// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Identifies the user-assigned identity that Azure Dev Compute uses to pull sandbox images.
/// </summary>
/// <param name="identity">The user-assigned identity resource.</param>
/// <param name="isAspireManaged">Whether Aspire created the identity for this sandbox group.</param>
internal sealed class AzureSandboxGroupAcrPullIdentityAnnotation(
    AzureUserAssignedIdentityResource identity,
    bool isAspireManaged = false) : IAcrPullIdentityAnnotation
{
    /// <summary>
    /// Gets the user-assigned identity resource.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;

    /// <summary>
    /// Gets a value indicating whether Aspire created the identity for this sandbox group.
    /// </summary>
    public bool IsAspireManaged { get; } = isAspireManaged;
}
