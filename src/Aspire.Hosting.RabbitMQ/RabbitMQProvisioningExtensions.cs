// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class RabbitMQProvisioningExtensions
{
    internal static IResourceBuilder<T> WithRabbitMQTopologyWiring<T>(this IResourceBuilder<T> builder, IResource directParent)
        where T : RabbitMQProvisionableResource, IRabbitMQServerChild
    {
        var server = builder.Resource.VirtualHost.Parent;
        var serverBuilder = builder.ApplicationBuilder.CreateResourceBuilder(server);

        switch (builder.Resource)
        {
            case RabbitMQVirtualHostResource vhost when !vhost.IsDefault:
                serverBuilder.WithManagementPlugin();
                break;

            case RabbitMQPolicyResource:
                serverBuilder.WithManagementPlugin();
                break;

            case RabbitMQShovelResource:
                serverBuilder
                    .WithManagementPlugin()
                    .WithPlugin(RabbitMQPlugin.Shovel)
                    .WithPlugin(RabbitMQPlugin.ShovelManagement);
                break;
        }

        return RabbitMQBuilderExtensions.WithProvisionableHealthCheck(builder)
            .WithParentRelationship(directParent)
            .WithRabbitMQParentLifecycle(directParent)
            .WithRabbitMQTopologyCommands(directParent);
    }

    internal static IResourceBuilder<T> WithRabbitMQParentLifecycle<T>(this IResourceBuilder<T> builder, IResource directParent)
        where T : RabbitMQProvisionableResource, IRabbitMQServerChild
    {
        var child = builder.Resource;
        var parentServer = child.VirtualHost.Parent;
        var eventing = builder.ApplicationBuilder.Eventing;

        builder.WithInitialState(new CustomResourceSnapshot
        {
            State = KnownResourceStates.NotStarted,
            ResourceType = typeof(T).Name,
            Properties = []
        });

        builder.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(directParent, async (evt, ct) =>
        {
            var services = evt.Services;
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(child);
            var client = services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(parentServer.Name);

            logger.LogDebug(
                "[RabbitMQ lifecycle] ResourceReadyEvent received for '{ChildName}' (parent: '{ParentName}'). Starting reconcile.",
                child.Name, directParent.Name);

            var reconcileToken = child.ReconcileGate.BeginNew(ct);

            await StartCore(child, client, notifications, services, eventing, logger, reconcileToken).ConfigureAwait(false);
        });

        builder.ApplicationBuilder.Eventing.Subscribe<ResourceStoppedEvent>(directParent, async (evt, ct) =>
        {
            var services = evt.Services;
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(child);
            var client = services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(parentServer.Name);

            logger.LogDebug(
                "[RabbitMQ lifecycle] ResourceStoppedEvent received for '{ChildName}' (parent: '{ParentName}'). Cascading stop.",
                child.Name, directParent.Name);

            await StopCore(child, client, notifications, services, eventing, logger, parentServer.Name, ct).ConfigureAwait(false);
        });

        return builder;
    }

    internal static IResourceBuilder<T> WithRabbitMQTopologyCommands<T>(this IResourceBuilder<T> builder, IResource directParent)
        where T : RabbitMQProvisionableResource, IRabbitMQServerChild
    {
        var child = builder.Resource;
        var parentServer = child.VirtualHost.Parent;
        var eventing = builder.ApplicationBuilder.Eventing;
        var directParentName = directParent.Name;

        builder.WithAnnotation(new ResourceCommandAnnotation(
            name: KnownResourceCommands.StartCommand,
            displayName: "Start",
            updateState: context =>
            {
                if (!IsInStoppedState(context.ResourceSnapshot.State?.Text))
                {
                    return ResourceCommandState.Disabled;
                }

                var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
                return IsResourceRunning(notifications, directParentName)
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Disabled;
            },
            executeCommand: async context =>
            {
                try
                {
                    await StartCommandAsync(context, child, parentServer.Name, eventing).ConfigureAwait(false);
                    return CommandResults.Success();
                }
                catch (Exception ex)
                {
                    LogCommandFailure(context, child, "start", ex);
                    return CommandResults.Failure(ex);
                }
            },
            displayDescription: "Recreates this RabbitMQ topology resource on the broker.",
            parameter: null,
            confirmationMessage: null,
            iconName: "Play",
            iconVariant: IconVariant.Filled,
            isHighlighted: true));

        builder.WithAnnotation(new ResourceCommandAnnotation(
            name: KnownResourceCommands.StopCommand,
            displayName: "Stop",
            updateState: context => IsRunning(context.ResourceSnapshot.State?.Text)
                ? ResourceCommandState.Enabled
                : ResourceCommandState.Disabled,
            executeCommand: async context =>
            {
                try
                {
                    await StopCommandAsync(context, child, parentServer.Name, eventing).ConfigureAwait(false);
                    return CommandResults.Success();
                }
                catch (Exception ex)
                {
                    LogCommandFailure(context, child, "stop", ex);
                    return CommandResults.Failure(ex);
                }
            },
            displayDescription: "Deletes this RabbitMQ topology resource from the broker.",
            parameter: null,
            confirmationMessage: null,
            iconName: "Stop",
            iconVariant: IconVariant.Filled,
            isHighlighted: true));

        builder.WithAnnotation(new ResourceCommandAnnotation(
            name: KnownResourceCommands.RestartCommand,
            displayName: "Restart",
            updateState: context => IsRunning(context.ResourceSnapshot.State?.Text)
                ? ResourceCommandState.Enabled
                : ResourceCommandState.Disabled,
            executeCommand: async context =>
            {
                try
                {
                    await StopCommandAsync(context, child, parentServer.Name, eventing).ConfigureAwait(false);
                    await StartCommandAsync(context, child, parentServer.Name, eventing).ConfigureAwait(false);
                    return CommandResults.Success();
                }
                catch (Exception ex)
                {
                    LogCommandFailure(context, child, "restart", ex);
                    return CommandResults.Failure(ex);
                }
            },
            displayDescription: "Deletes and recreates this RabbitMQ topology resource on the broker.",
            parameter: null,
            confirmationMessage: null,
            iconName: "ArrowCounterclockwise",
            iconVariant: IconVariant.Regular,
            isHighlighted: false));

        return builder;
    }

    private static async Task StartCommandAsync(ExecuteCommandContext context, RabbitMQProvisionableResource child, string serverName, IDistributedApplicationEventing eventing)
    {
        var services = context.Services;
        var notifications = services.GetRequiredService<ResourceNotificationService>();
        var logger = context.Logger;
        var client = services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(serverName);

        logger.LogDebug("[RabbitMQ command] Start command invoked for '{ResourceName}'.", child.Name);

        var reconcileToken = child.ReconcileGate.BeginNew(CancellationToken.None);

        await StartCore(child, client, notifications, services, eventing, logger, reconcileToken).ConfigureAwait(false);
    }

    private static async Task StopCommandAsync(ExecuteCommandContext context, RabbitMQProvisionableResource child, string serverName, IDistributedApplicationEventing eventing)
    {
        var services = context.Services;
        var notifications = services.GetRequiredService<ResourceNotificationService>();
        var logger = context.Logger;
        var client = services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(serverName);

        logger.LogDebug("[RabbitMQ command] Stop command invoked for '{ResourceName}'.", child.Name);

        child.ReconcileGate.CancelCurrent();

        await StopCore(child, client, notifications, services, eventing, logger, serverName, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task StartCore(
        RabbitMQProvisionableResource child,
        IRabbitMQProvisioningClient client,
        ResourceNotificationService notifications,
        IServiceProvider services,
        IDistributedApplicationEventing eventing,
        ILogger logger,
        CancellationToken reconcileToken)
    {
        try
        {
            await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.Waiting }).ConfigureAwait(false);
            await notifications.WaitForDependenciesAsync(child, reconcileToken).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);

            logger.LogDebug("[RabbitMQ lifecycle] Reconciling '{ResourceName}'.", child.Name);

            await child.ReconcileAsync(client, reconcileToken).ConfigureAwait(false);

            await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.Running }).ConfigureAwait(false);

            logger.LogDebug("[RabbitMQ lifecycle] '{ResourceName}' is now Running.", child.Name);

            await eventing.PublishAsync(new ResourceReadyEvent(child, services), reconcileToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reconcileToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "[RabbitMQ lifecycle] Reconcile for '{ResourceName}' was cancelled (parent stopped or superseded).",
                child.Name);
        }
        catch (Exception ex) when (reconcileToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "[RabbitMQ lifecycle] Reconcile for '{ResourceName}' interrupted by broker shutdown ({ExType}); treating as cancellation.",
                child.Name, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RabbitMQ lifecycle] Failed to reconcile RabbitMQ resource '{ResourceName}'.", child.Name);
            await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
        }
    }

    private static async Task StopCore(
        RabbitMQProvisionableResource child,
        IRabbitMQProvisioningClient client,
        ResourceNotificationService notifications,
        IServiceProvider services,
        IDistributedApplicationEventing eventing,
        ILogger logger,
        string serverName,
        CancellationToken cancellationToken)
    {
        child.ReconcileGate.CancelCurrent();

        await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.Stopping }).ConfigureAwait(false);

        if (IsResourceRunning(notifications, serverName))
        {
            try
            {
                logger.LogDebug("[RabbitMQ lifecycle] Deleting '{ResourceName}' from broker.", child.Name);
                await child.DeleteAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[RabbitMQ lifecycle] Delete of '{ResourceName}' from broker failed (broker may have gone down).",
                    child.Name);
            }
        }
        else
        {
            logger.LogDebug(
                "[RabbitMQ lifecycle] Skipping delete of '{ResourceName}' — broker (server '{ServerName}') is not Running.",
                child.Name, serverName);
        }

        await notifications.PublishUpdateAsync(child, s => s with { State = KnownResourceStates.Exited }).ConfigureAwait(false);

        logger.LogDebug(
            "[RabbitMQ lifecycle] '{ResourceName}' is now Exited. Cascading ResourceStoppedEvent to children.",
            child.Name);

        var childSnapshot = new CustomResourceSnapshot
        {
            ResourceType = child.GetType().Name,
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Exited, null)
        };
        var childResourceEvent = new ResourceEvent(child, child.Name, childSnapshot);
        await eventing.PublishAsync(
            new ResourceStoppedEvent(child, services, childResourceEvent),
            cancellationToken).ConfigureAwait(false);
    }

    private static void LogCommandFailure(ExecuteCommandContext context, RabbitMQProvisionableResource child, string operation, Exception ex)
        => context.Logger.LogError(ex, "Failed to {Operation} RabbitMQ resource '{ResourceName}'.", operation, child.Name);

    private static bool IsResourceRunning(ResourceNotificationService notifications, string resourceName)
        => notifications.TryGetCurrentState(resourceName, out var evt)
           && evt.Snapshot.State?.Text == KnownResourceStates.Running;

    private static bool IsInStoppedState(string? state)
        => KnownResourceStates.TerminalStates.Contains(state) || state == KnownResourceStates.NotStarted;

    private static bool IsRunning(string? state) => state == KnownResourceStates.Running;
}
