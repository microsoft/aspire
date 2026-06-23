// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An annotation that points to the resource that contains the role assignments for an Azure resource.
/// </summary>
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
internal sealed class RoleAssignmentResourceAnnotation(AzureRoleAssignmentResource rolesResource) : IResourceAnnotation
{
    /// <summary>
    /// The resource that contains the role assignments for the Azure resource.
    /// </summary>
    public AzureRoleAssignmentResource RolesResource { get; } = rolesResource;
}
#pragma warning restore ASPIREAZURE003
