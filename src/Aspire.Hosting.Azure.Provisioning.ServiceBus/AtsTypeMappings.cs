// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.ServiceBus;

[assembly: GenerateAspireProvisioningProxy(
    typeof(ServiceBusNamespace),
    ExcludedMemberNames = new[]
    {
        // BicepDictionary<object> cannot be projected because arbitrary CLR values have no type-safe ATS representation.
        "ApplicationProperties"
    })]
[assembly: GenerateAspireProvisioningProxy(typeof(ServiceBusQueue), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ServiceBusTopic), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ServiceBusSubscription), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ServiceBusRule), IsInfrastructureRoot = false)]
