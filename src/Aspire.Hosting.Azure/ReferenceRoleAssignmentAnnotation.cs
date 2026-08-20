// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Declares that any compute resource referencing the annotated resource should be granted
/// <see cref="Roles"/> on the Azure resource <see cref="Target"/>.
/// </summary>
/// <param name="target">The Azure resource that referencing resources should be granted roles on.</param>
/// <param name="roles">The roles that referencing resources should be assigned on <paramref name="target"/>.</param>
/// <remarks>
/// <para>
/// This annotation is applied to a resource that "fronts" an Azure resource without being an
/// <see cref="IAzureResource"/> itself. For example, a Foundry hosted agent's node app is a plain
/// compute resource, but invoking the agent requires the caller to hold a role on the owning
/// Foundry account. The account is only a transitive dependency of a consumer, so
/// <see cref="AzureResourcePreparer"/>'s normal reference walk — which only acts on direct
/// <see cref="IAzureResource"/> dependencies — cannot reach it.
/// </para>
/// <para>
/// When a compute resource takes a direct dependency on a resource carrying this annotation,
/// <see cref="AzureResourcePreparer"/> folds <c>(Target, Roles)</c> into the same role-assignment
/// path used for direct Azure references, so the consumer gets a managed identity and the
/// corresponding role assignment on <see cref="Target"/> with no additional wiring.
/// </para>
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ReferenceRoleAssignmentAnnotation(AzureProvisioningResource target, IReadOnlySet<RoleDefinition> roles) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Azure resource that resources referencing the annotated resource should be granted roles on.
    /// </summary>
    public AzureProvisioningResource Target { get; } = target;

    /// <summary>
    /// Gets the set of roles that resources referencing the annotated resource should be assigned on <see cref="Target"/>.
    /// </summary>
    public IReadOnlySet<RoleDefinition> Roles { get; } = roles;
}
