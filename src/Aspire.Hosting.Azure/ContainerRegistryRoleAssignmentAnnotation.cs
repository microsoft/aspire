// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the roles that the current resource's configured container registry should receive for the current resource identity.
/// </summary>
/// <param name="roles">The roles that the current resource identity should be assigned on the configured container registry.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerRegistryRoleAssignmentAnnotation(IReadOnlySet<RoleDefinition> roles) : IResourceAnnotation
{
    /// <summary>
    /// Gets the set of roles the current resource identity should be assigned on the configured container registry.
    /// </summary>
    public IReadOnlySet<RoleDefinition> Roles { get; } = roles ?? throw new ArgumentNullException(nameof(roles));
}
