// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

internal sealed class DefaultRoleAssignmentCollection(RoleAssignmentCollection roleAssignmentCollection) : IRoleAssignmentCollection
{
    public Task<ArmOperation<RoleAssignmentResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string roleAssignmentName,
        RoleAssignmentCreateOrUpdateContent content,
        CancellationToken cancellationToken = default)
    {
        return roleAssignmentCollection.CreateOrUpdateAsync(waitUntil, roleAssignmentName, content, cancellationToken);
    }
}
