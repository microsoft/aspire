// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

internal sealed record AzureApiManagementOpenAIBackend(
    string Name,
    AzureProvisioningResource Account,
    ReferenceExpression Endpoint,
    string DeploymentName,
    int Priority,
    int Weight);
