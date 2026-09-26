// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning.Sql;

[assembly: GenerateAspireProvisioningProxy(typeof(SqlServer), IncludeContainingAssemblyTypes = true)]
[assembly: GenerateAspireProvisioningProxy(typeof(SqlDatabase), IsInfrastructureRoot = false)]
[assembly: GenerateAspireProvisioningProxy(typeof(ServerExternalAdministrator), IsInfrastructureRoot = false)]
