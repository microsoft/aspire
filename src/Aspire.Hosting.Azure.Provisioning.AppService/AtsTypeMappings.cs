// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.AppService;

[assembly: GenerateAspireProvisioningProxy(
    typeof(AppServicePlan),
    IncludeContainingAssemblyTypes = true)]
[assembly: GenerateAspireProvisioningProxy(typeof(WebSite))]
