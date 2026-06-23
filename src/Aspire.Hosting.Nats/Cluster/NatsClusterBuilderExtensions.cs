// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.NATS.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.Core;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding NATS cluster resources to the application model.
/// </summary>
public static class NatsClusterBuilderExtensions
{
    /// <summary>
    /// Adds a NATS cluster resource to the application model.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<NatsClusterResource> AddNatsCluster(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var natsCluster = new NatsClusterResource(name);

        var connectionString = null as string;

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                healthCheckKey,
                sp => new NatsHealthCheck(new NatsConnection(new()
                {
                    Url = connectionString!,
                })),
                failureStatus: default,
                tags: default,
                timeout: default
            ));

        return builder.AddResource(natsCluster)
            .WithHealthCheck(healthCheckKey)
            .WithInitialState(new()
            {
                ResourceType = "NATS Cluster",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties = [],
            })
            .OnInitializeResource(async (resource, @event, ct) =>
            {
                await @event.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Starting,
                }).ConfigureAwait(false);

                connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                    ?? throw new DistributedApplicationException($"{nameof(ConnectionStringAvailableEvent)} was published for the '{resource.Name}' resource but the connection string was null.");

                await @event.Eventing.PublishAsync(new ConnectionStringAvailableEvent(resource, @event.Services), ct)
                    .ConfigureAwait(false);

                await @event.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Running,
                }).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Adds a NATS server resource as a member of NATS cluster.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="member"></param>
    /// <returns></returns>
    [AspireExport]
    public static IResourceBuilder<NatsClusterResource> WithMember(
        this IResourceBuilder<NatsClusterResource> builder,
        IResourceBuilder<NatsServerResource> member
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(member);

        member.WithCluster(
            clusterName: builder.Resource.Name,
            routes: [.. builder.Resource.Members.Select(m => m.ClusterEndpoint)]
        );
        if (member.Resource.TryGetAnnotationsOfType<NatsJetStreamAnnotation>(out _))
        {
            member.WithServerName();
        }

        return builder
            .WithAnnotation(new NatsClusterMemberAnnotation(member.Resource))
            .WaitFor(member)
            .WithRelationship(member, "cluster member");
    }
}

internal sealed record NatsClusterMemberAnnotation(
    NatsServerResource Member
) : IResourceAnnotation;
