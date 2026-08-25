// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.EventHubs;

[assembly: GenerateAspireProvisioningProxy(
    typeof(EventHubsNamespace),
    ExcludedMemberNames = new[]
    {
        "GetResourceNameRequirements",
        "SystemData"
    })]
[assembly: GenerateAspireProvisioningProxy(typeof(EventHub), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(EventHubsConsumerGroup), IsInfrastructureRoot = false)]
