// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.ContainerRegistry;

[assembly: GenerateAspireProvisioningProxy(typeof(ContainerRegistryService), IncludeContainingAssemblyTypes = true)]
[assembly: GenerateAspireProvisioningProxy(typeof(ContainerRegistryTask), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ContainerRegistryEncodedTaskStep))]
[assembly: GenerateAspireProvisioningProxy(typeof(ContainerRegistryTimerTrigger))]
