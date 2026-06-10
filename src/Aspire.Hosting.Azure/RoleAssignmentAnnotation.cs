// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the roles that the current resource should be assigned to the target Azure resource.
/// </summary>
/// <remarks>
/// <para>
/// This annotation is most commonly applied to compute resources (for example, projects or containers) that need to
/// interact with Azure resources.
/// </para>
/// <para>
/// Aggregate resources can also use this annotation when they own internal Azure resources and need the Azure
/// preparer to create role-assignment infrastructure on behalf of those internals.
/// </para>
/// </remarks>
public class RoleAssignmentAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoleAssignmentAnnotation"/> class.
    /// </summary>
    /// <param name="target">The Azure resource that the current resource will interact with.</param>
    /// <param name="roles">The roles that the current resource should be assigned to <paramref name="target"/>.</param>
    public RoleAssignmentAnnotation(AzureProvisioningResource target, IReadOnlySet<RoleDefinition> roles)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(roles);

        Target = target;
        Roles = roles;
    }

    /// <summary>
    /// The Azure resource that the current resource will interact with.
    /// </summary>
    public AzureProvisioningResource Target { get; }

    /// <summary>
    /// Gets the set of roles the current resource should be assigned to the target Azure resource.
    /// </summary>
    public IReadOnlySet<RoleDefinition> Roles { get; }
}
