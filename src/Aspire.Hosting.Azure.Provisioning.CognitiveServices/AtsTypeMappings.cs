// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;

[assembly: GenerateAspireProvisioningProxy(
    typeof(CognitiveServicesAccount),
    ExcludedMemberNames = new[]
    {
        "AdditionalProperties",
        "CreateRoleAssignment",
        "GetResourceNameRequirements",
        "Identity",
        "SystemData"
    })]
[assembly: GenerateAspireProvisioningProxy(typeof(CognitiveServicesAccountDeployment), IsInfrastructureRoot = false)]
