// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding MongoDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class MongoDBReplicaSetBuilderExtensions
{
    private const string ReplicaSetAlreadyInitializedCodeName = "AlreadyInitialized";
    private const string ReplicaSetNotYetInitializedCodeName = "NotYetInitialized";
    private const string NewReplicaSetConfigurationIsTooOldCodeName = "NewReplicaSetConfigurationIsTooOld";
    private const int MaxRetriesAttempt = 10;
    private static readonly TimeSpan s_rsInitiationRetryWaitInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Adds a MongoDB replica set resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to which the replica set resource will be added.</param>
    /// <param name="name">The name of the replica set resource.</param>
    /// <param name="userName">An optional parameter resource that contains the username for authenticating to the MongoDB replica set. If not provided, a default username will be used.</param>
    /// <param name="password">An optional parameter resource that contains the password for authenticating to the MongoDB replica set. If not provided, a default password will be used.</param>
    /// <remarks>
    /// This is a "logical" resource that groups multiple <see cref="MongoDBServerResource"/> instances that are annotated as members of the replica set.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBReplicaSetResource> AddMongoDBReplicaSet(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var rsResource = new MongoDBReplicaSetResource(
            name: name,
            keyFile: ParameterResourceBuilderExtensions.CreateGeneratedParameter(
                builder,
                $"{name}-keyfile-content",
                secret: true,
                new GenerateParameterDefault
                {
                    MinLength = 512, // NOTE: MongoDB requires the key file content to be between 6 and 1024 characters — see https://www.mongodb.com/docs/manual/tutorial/deploy-replica-set-with-keyfile-access-control/#create-a-keyfile
                    Special = false,
                }
            ),
            sharedUserName: userName?.Resource,
            sharedPassword: password?.Resource
                ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false)
        );

        var connectionString = null as string;
        var healthCheckKey = $"{name}_check";

        builder.Services.AddHealthChecks()
            .AddMongoDb(
                sp => new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable")),
                name: healthCheckKey);

        return builder.AddResource(rsResource)
            .WithHealthCheck(healthCheckKey)
            .WithInitialState(new()
            {
                ResourceType = "MongoDB Replica Set",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties = [],
            })
            .OnInitializeResource(async (resource, evt, ct) =>
            {
                var logger = evt.Services.GetRequiredService<ILogger<MongoDBReplicaSetResource>>();

                var membersList = rsResource.Members.ToList();
                if (membersList is [])
                {
                    await evt.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.FailedToStart,
                    }).ConfigureAwait(false);
                    logger.LogCritical("Cannot initialize MongoDB replica set resource '{ResourceName}' because it does not have any members.", resource.Name);
                    return;
                }

                connectionString = await rsResource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(resource, evt.Services), ct)
                    .ConfigureAwait(false);

                await evt.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Starting,
                }).ConfigureAwait(false);

                var initialPrimary = membersList.First();
                var connectionStringToPrimary = await initialPrimary.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                using var primaryClient = new MongoClient(connectionStringToPrimary);

                var membersBsonArray = new BsonArray(
                    await Task.WhenAll(membersList.Select(async (m, i) => new BsonDocument
                    {
                        ["_id"] = i,
                        // NOTE: `host` represents the host and port that should be accessible from within the MongoDB server's container.
                        ["host"] = $"{m.Name}:{m.PrimaryEndpoint.TargetPort!.Value}", // NOTE: We know this is always set.
                        // NOTE: `horizons` is a poorly-documented but quite essential MongoDB feature when it comes to clustering — see https://github.com/mongodb/mongo/tree/master/src/mongo/db/repl/split_horizon as well as https://www.percona.com/blog/using-replicasethorizons-in-mongodb/
                        ["horizons"] = new BsonDocument
                        {
                            // NOTE: This represents the host and port that would actually be advertised to outside clients, and should as such be accessible from outside the MongoDB server's container
                            // NOTE: The property name (`external`) here is purely informational, what matters is the value and specifically whether or not the hostname in the value matches the SNI of the incoming client connections.
                            ["external"] = await m.PrimaryEndpoint
                                .Property(EndpointProperty.HostAndPort)
                                .GetValueAsync(ct)
                                .ConfigureAwait(false),
                        }
                    })).ConfigureAwait(false)
                );

                var rsConfigAttempts = 0;
                var admin = primaryClient.GetDatabase("admin");
                while (true)
                {
                    try
                    {
                        logger.LogInformation("Retrieving MongoDB replica set information ({ResourceName}) from the primary", resource.Name);
                        var currentConfig = await admin.RunCommandAsync<BsonDocument>(
                            command: new BsonDocument
                            {
                                ["replSetGetConfig"] = 1,
                            },
                            cancellationToken: ct
                        ).ConfigureAwait(false);

                        var version = currentConfig["config"]["version"].AsInt32;

                        logger.LogInformation("Re-configuring MongoDB replica set resource '{ResourceName}' — last version {Version}", resource.Name, version);
                        await admin.RunCommandAsync<BsonDocument>(
                            command: new BsonDocument
                            {
                                ["replSetReconfig"] = new BsonDocument
                                {
                                    ["version"] = version + 1,
                                    ["members"] = membersBsonArray,
                                },
                            },
                            cancellationToken: ct
                        ).ConfigureAwait(false);
                        break;
                    }
                    catch (MongoCommandException ex) when (ex.CodeName is NewReplicaSetConfigurationIsTooOldCodeName && rsConfigAttempts++ < MaxRetriesAttempt)
                    {
                        // NOTE: Happens when another concurrent process has already updated the replica set configuration with a higher version. We need to re-fetch the current configuration and retry with an updated version number.
                        logger.LogInformation("Reconfiguring the replica set failed due to another concurrent process doing the same — retry attempt {Current}/{Max} to begin after {WaitIntervalSeconds} seconds", rsConfigAttempts, MaxRetriesAttempt, s_rsInitiationRetryWaitInterval.TotalSeconds);
                        await Task.Delay(s_rsInitiationRetryWaitInterval, ct).ConfigureAwait(false);
                    }
                    catch (MongoCommandException ex) when (ex.CodeName is ReplicaSetNotYetInitializedCodeName)
                    {
                        logger.LogInformation("Initializing MongoDB replica set resource '{ResourceName}'", resource.Name);
                        var replicaSetInitiateCommand = new BsonDocument
                        {
                            ["replSetInitiate"] = new BsonDocument
                            {
                                ["_id"] = rsResource.Name,
                                ["members"] = membersBsonArray,
                            }
                        };

                        try
                        {
                            await admin.RunCommandAsync<BsonDocument>(replicaSetInitiateCommand, cancellationToken: ct).ConfigureAwait(false);
                            break;
                        }
                        catch (MongoCommandException initiateEx) when (initiateEx.CodeName is ReplicaSetAlreadyInitializedCodeName && rsConfigAttempts++ < MaxRetriesAttempt)
                        {
                            // NOTE: Happens when in race with another concurrent process trying to initialize the replica set; so we retry the whole operation
                            logger.LogInformation("Initiating the replica set failed due to it already being initialized — retry attempt {Current}/{Max} to begin after {WaitIntervalSeconds} seconds", rsConfigAttempts, MaxRetriesAttempt, s_rsInitiationRetryWaitInterval.TotalSeconds);
                            await Task.Delay(s_rsInitiationRetryWaitInterval, ct).ConfigureAwait(false);
                        }
                    }
                }

                await evt.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Running,
                }).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Adds a MongoDB server resource as a member of the replica set.
    /// </summary>
    /// <param name="builder">
    /// The <see cref="IResourceBuilder{MongoDBReplicaSetResource}"/> to which the member will be added.
    /// </param>
    /// <param name="member">
    /// The MongoDB server resource that represents the member to add to this replica set.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Internally calls three methods on the member's builder:
    /// <list type="number">
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithReplicaSet(IResourceBuilder{MongoDBServerResource}, string)"/> to set the replica set name on the member resource and configure it accordingly. </description></item>
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithTls(IResourceBuilder{MongoDBServerResource}, MongoDBTlsMode)"/> to enable TLS on the member resource, which is required for the split-horizon member hostname advertisement by the server. </description></item>
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithKeyFile(IResourceBuilder{MongoDBServerResource}, IExpressionValue, string)"/> to set the key file parameter on the member resource, which is required for internal authentication between replica set members. </description></item>
    /// </list>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBReplicaSetResource> WithMember(
        this IResourceBuilder<MongoDBReplicaSetResource> builder,
        IResourceBuilder<MongoDBServerResource> member
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(member);

        member
            .WithReplicaSet(builder.Resource.Name)
            .WithTls() // NOTE: TLS is actually necessary here, because the `horizons` feature used for initializing the replica set operates on top of SNI, which requires client-to-server TLS to be enabled.
            .WithKeyFile(builder.Resource.SharedKeyFileParameter);

        // NOTE: Even if we don't do this, the primary will propagate its username/password credentials to the other members, but we make sure to model this at the level of the resource graph so that the connection strings to individual replica-set members would contain the correct credentials if they are used directly (e.g. for health checks or other purposes).
        member.Resource.UserNameParameter = builder.Resource.SharedUserNameParameter;
        member.Resource.PasswordParameter = builder.Resource.SharedPasswordParameter;

        return builder
            .WithAnnotation(new MongoReplicaSetMemberAnnotation(member.Resource))
            .WaitFor(member)
            .WithRelationship(member, "replica set member");
    }
}

internal sealed record MongoReplicaSetMemberAnnotation(
    MongoDBServerResource Member
) : IResourceAnnotation;
