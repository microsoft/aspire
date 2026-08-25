// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;

[assembly: GenerateAspireProvisioningProxy(
    typeof(CognitiveServicesAccount),
    ExcludedMemberNames = new[]
    {
        // BicepDictionary<BinaryData> cannot be projected because BinaryData is an opaque JSON payload with no ATS-compatible representation.
        "AdditionalProperties"
    })]
[assembly: GenerateAspireProvisioningProxy(typeof(CognitiveServicesAccountDeployment), IsInfrastructureRoot = false)]
