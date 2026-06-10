// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Indicates that an <see cref="AzureAppServiceEnvironmentResource"/> should use the supplied
/// <see cref="AzureUserAssignedIdentityResource"/> as the identity that holds the <c>AcrPull</c> role on the
/// configured container registry, instead of having Aspire create a new identity and a new <c>AcrPull</c>
/// role assignment.
/// </summary>
/// <param name="identity">The user-assigned identity resource to use for container registry pulls.</param>
/// <param name="assignAcrPullRole">Indicates whether Aspire should assign the <c>AcrPull</c> role to the identity.</param>
internal sealed class AzureAppServiceEnvironmentAcrPullIdentityAnnotation(
    AzureUserAssignedIdentityResource identity,
    bool assignAcrPullRole) : IResourceAnnotation
{
    /// <summary>
    /// Gets the user-assigned identity resource used for container registry pulls.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;

    /// <summary>
    /// Gets a value indicating whether Aspire should assign the <c>AcrPull</c> role to the identity.
    /// </summary>
    public bool AssignAcrPullRole { get; } = assignAcrPullRole;
}
