// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

// Provisions azure resources for development purposes
internal sealed class AzureProvisioner(
    AzureProvisioningController provisioningController)
{
    internal Task ProvisionResourcesAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        return provisioningController.EnsureProvisionedAsync(model, cancellationToken);
    }
}
