// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.Storage;

[assembly: GenerateAspireProvisioningProxy(
    typeof(StorageAccount),
    IncludeContainingAssemblyTypes = true,
    ExcludedMemberNames = new[]
    {
        // Export the replacement SDK members rather than obsolete aliases.
        "Azure.Provisioning.Storage.ExecutionTrigger.TriggerType",
        "Azure.Provisioning.Storage.FileShare.AccessTierChangeOn",
        "Azure.Provisioning.Storage.StorageAccount.PrivateEndpointConnections",
        "Azure.Provisioning.Storage.StorageAccount.ProvisioningState",
        "Azure.Provisioning.Storage.StorageServiceAccessPolicy.StartOn",
        "Azure.Provisioning.Storage.StorageTableAccessPolicy.StartOn",
        "Azure.Provisioning.Storage.StorageTaskAssignmentProperties.ProvisioningState"
    })]
[assembly: GenerateAspireProvisioningProxy(typeof(Azure.Provisioning.Storage.FileShare), IsInfrastructureRoot = false)]
