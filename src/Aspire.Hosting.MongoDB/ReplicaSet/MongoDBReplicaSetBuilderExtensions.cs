// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
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

    /// <summary>
    /// Adds a MongoDB replica set resource to the application model.
    /// </summary>
    /// <remarks>
    /// This is a "logical" resource that groups multiple <see cref="MongoDBServerResource"/> instances that are annotated as members of the replica set.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBReplicaSetResource> AddMongoDBReplicaSet(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name
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
                    MinLength = 512,
                    Special = false,
                }
            )
        );

        // var healthCheckKey = $"{name}_check";
        // todo
        // builder.Services.AddHealthChecks()
        //     .AddCheck(
        //         name: healthCheckKey,
        //         check: () => wasInitialized
        //             ? HealthCheckResult.Healthy()
        //             : HealthCheckResult.Unhealthy("Replica set not yet initialized")
        //     );

        return builder.AddResource(rsResource)
            // .WithHealthCheck(healthCheckKey)
            .WithInitialState(new()
            {
                ResourceType = "MongoDB Replica Set",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties = [],
            })
            .OnInitializeResource(async (resource, evt, ct) =>
            {
                var connectionString = await rsResource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(resource, evt.Services), ct)
                    .ConfigureAwait(false);

                await evt.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Starting,
                }).ConfigureAwait(false);

                if (!rsResource.TryGetAnnotationsOfType<MongoReplicaSetMemberAnnotation>(out var memberAnnotations) || !memberAnnotations.Any())
                {
                    return;
                }

                var initialPrimary = memberAnnotations.First().Member;
                var connectionStringToPrimary = await initialPrimary.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                var primaryClient = new MongoClient(connectionStringToPrimary);

                var membersBsonArray = new BsonArray(
                    // todo
                    [
                        new BsonDocument
                        {
                            ["_id"] = 0,
                            // NOTE: `host` represents the host and port that should be accessible from within the MongoDB server's container.
                            ["host"] = $"{initialPrimary.Name}:{initialPrimary.PrimaryEndpoint.TargetPort!.Value}", // NOTE: We know this is always set.
                            ["horizons"] = new BsonDocument
                            {
                                // NOTE: This represents the host and port that would actually be advertised to outside clients, and should as such be accessible from outside the MongoDB server's container
                                ["external"] = await initialPrimary.PrimaryEndpoint
                                    .Property(EndpointProperty.HostAndPort)
                                    .GetValueAsync(ct)
                                    .ConfigureAwait(false),
                            }
                        }
                    ]
                // memberAnnotations.Select(async (m, i) => new BsonDocument
                // {
                //     ["_id"] = i,
                //     ["host"] = $"localhost:{m.Member.PrimaryEndpoint.TargetPort ?? 27017}",
                // })
                );

                var admin = primaryClient.GetDatabase("admin");
                while (true)
                {
                    try
                    {
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
                        catch (MongoCommandException initiateEx) when (initiateEx.CodeName is ReplicaSetAlreadyInitializedCodeName)
                        {
                            // NOTE: Happens when in race with another concurrent process trying to initialize the replica set
                            // NOTE: We retry the whole operation
                        }

                        var currentConfig = await admin.RunCommandAsync<BsonDocument>(
                            command: new BsonDocument
                            {
                                ["replSetGetConfig"] = 1,
                            },
                            cancellationToken: ct
                        ).ConfigureAwait(false);

                        var version = currentConfig["config"]["version"].AsInt32;

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
                    catch (MongoCommandException ex) when (ex.CodeName is NewReplicaSetConfigurationIsTooOldCodeName)
                    {
                        // NOTE: Happens when another concurrent process has already updated the replica set configuration with a higher version. We need to re-fetch the current configuration and retry with an updated version number.
                    }
                    catch (MongoCommandException ex) when (ex.CodeName is ReplicaSetNotYetInitializedCodeName)
                    {
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
                        catch (MongoCommandException initiateEx) when (initiateEx.CodeName is ReplicaSetAlreadyInitializedCodeName)
                        {
                            // NOTE: Happens when in race with another concurrent process trying to initialize the replica set
                            // NOTE: We retry the whole operation
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }

                var foo = new MongoClient(connectionString);
                var v = await foo.ListDatabaseNamesAsync(ct).ConfigureAwait(false);

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
    [AspireExport]
    public static IResourceBuilder<MongoDBReplicaSetResource> WithMember(
        this IResourceBuilder<MongoDBReplicaSetResource> builder,
        IResourceBuilder<MongoDBServerResource> member
    )
    {
        member
            .WithReplicaSet(builder.Resource.Name)
            .WithKeyFile(builder.Resource.SharedKeyFileParameter);

        return builder
            .WithAnnotation(new MongoReplicaSetMemberAnnotation(member.Resource))
            .WaitFor(member)
            .WithRelationship(member, "replica set member");
    }
}

internal sealed record MongoReplicaSetMemberAnnotation(
    MongoDBServerResource Member
) : IResourceAnnotation;
