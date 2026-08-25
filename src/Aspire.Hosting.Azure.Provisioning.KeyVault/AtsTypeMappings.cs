// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.KeyVault;

[assembly: GenerateAspireProvisioningProxy(
    typeof(KeyVaultService),
    ExcludedMemberNames = new[] { "CreateRoleAssignment", "GetResourceNameRequirements", "SystemData" })]
[assembly: GenerateAspireProvisioningProxy(typeof(KeyVaultSecret), IsInfrastructureRoot = false)]
