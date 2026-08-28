// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Identifies the user-assigned identity used to import sandbox images from the configured Azure Container Registry.
/// </summary>
/// <param name="identity">The user-assigned identity used for image pulls.</param>
internal sealed class AzureSandboxGroupAcrPullIdentityAnnotation(AzureUserAssignedIdentityResource identity) : IAcrPullIdentityAnnotation
{
    /// <summary>
    /// Gets the user-assigned identity used for image pulls.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;
}
