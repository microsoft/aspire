// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

internal sealed class DefaultArmDeploymentCollection(ArmDeploymentCollection armDeploymentCollection) : IArmDeploymentCollection
{
    public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string deploymentName,
        ArmDeploymentContent content,
        CancellationToken cancellationToken = default)
    {
        return armDeploymentCollection.CreateOrUpdateAsync(waitUntil, deploymentName, content, cancellationToken);
    }

    public async Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
    {
        var deployment = await armDeploymentCollection.GetAsync(deploymentName, cancellationToken).ConfigureAwait(false);
        await deployment.Value.CancelAsync(cancellationToken).ConfigureAwait(false);
    }
}
