// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.Network;

[assembly: GenerateAspireProvisioningProxy(
    typeof(VirtualNetwork),
    IncludeContainingAssemblyTypes = true,
    // The SDK's open-ended BinaryData dictionaries cannot be projected to typed ATS values.
    ExcludedMemberNames = new[] { "AdditionalProperties" })]
[assembly: GenerateAspireProvisioningProxy(typeof(NetworkSecurityGroup))]
[assembly: GenerateAspireProvisioningProxy(typeof(NatGateway))]
[assembly: GenerateAspireProvisioningProxy(typeof(PublicIPAddress))]
[assembly: GenerateAspireProvisioningProxy(typeof(PrivateEndpoint))]
[assembly: GenerateAspireProvisioningProxy(typeof(NetworkSecurityPerimeter))]
