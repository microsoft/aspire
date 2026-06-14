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
/// <param name="assignAcrPullRole">
/// <see langword="true"/> when the identity is Aspire-generated and Aspire is responsible for granting it the
/// <c>AcrPull</c> role on the environment's registry; <see langword="false"/> when the caller supplied their own
/// identity via <c>WithAcrPullIdentity</c> and therefore owns the role assignment.
/// </param>
internal sealed class AzureContainerAppEnvironmentAcrPullIdentityAnnotation(AzureUserAssignedIdentityResource identity, bool assignAcrPullRole = false) : IResourceAnnotation
{
    /// <summary>
    /// Gets the user-assigned identity resource that holds the <c>AcrPull</c> role.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;

    /// <summary>
    /// Gets a value indicating whether Aspire generated <see cref="Identity"/> and must grant it the
    /// <c>AcrPull</c> role on the environment's container registry.
    /// </summary>
    public bool AssignAcrPullRole { get; } = assignAcrPullRole;
}
