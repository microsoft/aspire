// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Azure;

// Provisions azure resources for development purposes
internal sealed class AzureProvisioner(
    AzureProvisioningController provisioningController) : IDistributedApplicationEventingSubscriber
{
    private Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        _ = provisioningController.EnsureProvisionedAsync(@event.Model, cancellationToken);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        }

        return Task.CompletedTask;
    }
}
