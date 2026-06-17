// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using Azure.Provisioning.Authorization;

namespace Aspire.Hosting.Azure;

internal sealed class AzureSandboxGroupRoleAssignmentPrincipal(
    string name,
    BicepOutputReference principalId,
    RoleManagementPrincipalType principalType,
    IReadOnlySet<AzureSandboxGroupBuiltInRole> roles)
{
    public string Name { get; } = name;

    public BicepOutputReference PrincipalId { get; } = principalId;

    public RoleManagementPrincipalType PrincipalType { get; } = principalType;

    public IReadOnlySet<AzureSandboxGroupBuiltInRole> Roles { get; } = roles;
}
