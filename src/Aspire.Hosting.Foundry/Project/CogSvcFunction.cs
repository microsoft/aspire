// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Foundry;

internal static class CogSvcFunction
{
    internal static ManagedServiceIdentity GetManagedServiceIdentity(IAppIdentityResource resource)
    {
        if (resource == null)
        {
            return new ManagedServiceIdentity
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned
            };
        }
        return new ManagedServiceIdentity
        {
            ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
            UserAssignedIdentities =
            {
                { BicepFunction.Interpolate($"{resource.Id}").Compile().ToString(), new UserAssignedIdentityDetails() }
            }
        };
    }

    internal static ManagedServiceIdentity GetManagedServiceIdentity(IEnumerable<IAppIdentityResource> resources)
    {
        if (resources == null || !resources.Any())
        {
            return new ManagedServiceIdentity
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned
            };
        }
        BicepDictionary<UserAssignedIdentityDetails> identities = [];
        foreach (var resource in resources)
        {
            identities[BicepFunction.Interpolate($"{resource.Id}").Compile().ToString()] = new UserAssignedIdentityDetails();
        }
        return new ManagedServiceIdentity
        {
            ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
            UserAssignedIdentities = identities
        };
    }
}
