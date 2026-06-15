// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Indicates that an <see cref="AppContainers.AzureContainerAppEnvironmentResource"/> should use the supplied
/// <see cref="AzureUserAssignedIdentityResource"/> as the identity that holds the <c>AcrPull</c> role on the
/// configured container registry, instead of having Aspire create a new identity and a new <c>AcrPull</c>
/// role assignment.
/// </summary>
/// <param name="identity">The user-assigned identity resource to use for the <c>AcrPull</c> role.</param>
internal sealed class AzureContainerAppEnvironmentAcrPullIdentityAnnotation(AzureUserAssignedIdentityResource identity) : IResourceAnnotation
{
    /// <summary>
    /// Gets the user-assigned identity resource that holds the <c>AcrPull</c> role.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;
}
