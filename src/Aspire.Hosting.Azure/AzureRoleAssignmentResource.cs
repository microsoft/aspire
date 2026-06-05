// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a set of Azure role assignments granted on a target Azure resource.
/// </summary>
/// <remarks>
/// When <see cref="OwnerResource"/> is non-<see langword="null"/>, the role assignments are granted to
/// the managed identity (<see cref="IdentityResource"/>) owned by that Aspire resource. When
/// <see cref="OwnerResource"/> is <see langword="null"/>, the role assignments are global and are
/// granted to the deployment principal.
/// </remarks>
/// <param name="name">The name of the resource in the distributed application model.</param>
/// <param name="targetAzureResource">The Azure resource that the roles are assigned on.</param>
/// <param name="ownerResource">The Aspire resource that owns this set of role assignments, or <see langword="null"/> for global role assignments granted to the deployment principal.</param>
/// <param name="identityResource">The user-assigned managed identity whose principal receives the role assignments, or <see langword="null"/> for global role assignments granted to the deployment principal.</param>
/// <param name="configureInfrastructure">Callback to configure the Azure role assignment resources.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureRoleAssignmentResource(
    string name,
    AzureProvisioningResource targetAzureResource,
    IResource? ownerResource,
    AzureUserAssignedIdentityResource? identityResource,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    /// <summary>
    /// Gets the Azure resource that the roles are assigned on.
    /// </summary>
    public AzureProvisioningResource TargetAzureResource { get; } = targetAzureResource ?? throw new ArgumentNullException(nameof(targetAzureResource));

    /// <summary>
    /// Gets the Aspire resource that owns this set of role assignments,
    /// or <see langword="null"/> for global role assignments that are granted to the deployment principal.
    /// </summary>
    /// <remarks>
    /// This is the resource on which <c>WithRoleAssignments</c> was called. Its managed identity
    /// is exposed via <see cref="IdentityResource"/>.
    /// When <c>WithRoleAssignments</c> is called using an <see cref="AzureUserAssignedIdentityResource"/>,
    /// OwnerResource and IdentityResource are the same.
    /// </remarks>
    public IResource? OwnerResource { get; } = ValidateOwnerAndIdentity(ownerResource, identityResource);

    /// <summary>
    /// Gets the user-assigned managed identity whose principal receives the role assignments,
    /// or <see langword="null"/> for global role assignments that are granted to the deployment principal.
    /// </summary>
    public AzureUserAssignedIdentityResource? IdentityResource { get; } = identityResource;

    private static IResource? ValidateOwnerAndIdentity(IResource? ownerResource, AzureUserAssignedIdentityResource? identityResource)
    {
        if ((ownerResource is null) != (identityResource is null))
        {
            throw new ArgumentException(
                $"'{nameof(ownerResource)}' and '{nameof(identityResource)}' must both be null (for global role assignments) or both be non-null (for targeted role assignments).",
                ownerResource is null ? nameof(ownerResource) : nameof(identityResource));
        }

        return ownerResource;
    }
}
