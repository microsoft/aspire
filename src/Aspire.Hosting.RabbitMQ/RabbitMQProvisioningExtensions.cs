// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class RabbitMQProvisioningExtensions
{
    /// <summary>
    /// Wires up decentralized provisioning for a <see cref="RabbitMQProvisionableResource"/> using
    /// the platform-standard <see cref="InitializeResourceEvent"/> lifecycle hook.
    /// </summary>
    /// <typeparam name="T">The concrete provisionable resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="dependencies">
    /// Resources that must be ready before provisioning begins.
    /// Each entry specifies the dependency resource and whether to wait until it is healthy or merely started.
    /// </param>
    /// <param name="provisionAsync">
    /// The provisioning action. Receives the resource, the provisioning client, a logger, and a cancellation token.
    /// Must throw on failure; the helper captures exceptions and transitions the resource to <c>FailedToStart</c>.
    /// </param>
    internal static IResourceBuilder<T> WithRabbitMQProvisioning<T>(
        this IResourceBuilder<T> builder,
        IReadOnlyList<(IResource Resource, WaitType WaitType)> dependencies,
        Func<T, IRabbitMQProvisioningClient, ILogger, CancellationToken, Task> provisionAsync)
        where T : RabbitMQProvisionableResource, IRabbitMQServerChild
    {
        builder.WithInitialState(new CustomResourceSnapshot
        {
            State = KnownResourceStates.NotStarted,
            ResourceType = typeof(T).Name,
            Properties = []
        });

        builder.OnInitializeResource(async (resource, evt, ct) =>
        {
            var notifications = evt.Notifications;
            var logger = evt.Logger;

            // Transition to Waiting while we wait for dependencies.
            await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Waiting }).ConfigureAwait(false);

            // Wait for all dependencies in parallel.
            if (dependencies.Count > 0)
            {
                var waitTasks = dependencies.Select(dep => dep.WaitType == WaitType.WaitUntilHealthy
                    ? notifications.WaitForResourceHealthyAsync(dep.Resource.Name, ct)
                    : notifications.WaitForResourceAsync(dep.Resource.Name, KnownResourceStates.Running, ct));

                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            }

            // Transition to Starting while the broker call is in progress.
            await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);

            try
            {
                var serverName = resource.VirtualHost.Parent.Name;
                var client = evt.Services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(serverName);
                await provisionAsync(resource, client, logger, ct).ConfigureAwait(false);
                await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Running }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Application is shutting down — do not log as error or transition to FailedToStart.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision RabbitMQ resource '{ResourceName}'.", resource.Name);
                await notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
            }
        });

        return builder;
    }
}
