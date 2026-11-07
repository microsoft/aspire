// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.AppContainers;

[assembly: GenerateAspireProvisioningProxy(
    typeof(ContainerAppManagedEnvironment),
    IncludeContainingAssemblyTypes = true,
    // These new SDK properties require raw JSON collections and arbitrary resource wrapping,
    // neither of which has a supported provisioning proxy representation.
    ExcludedMemberNames = [
        "Azure.Provisioning.AppContainers.ContainerAppDiagnosticDataTableResult.Rows",
        "Azure.Provisioning.AppContainers.LogicApp.Scope"
    ])]
// Publish callbacks use synthetic hosting resource identifiers rather than the app or job identifier.
[assembly: GenerateAspireProvisioningProxy(typeof(ContainerApp), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ContainerAppJob), IsInfrastructureRoot = false)]
