// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
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
                connectionString = await rsResource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

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
                    await Task.WhenAll(memberAnnotations.Select(async (m, i) => new BsonDocument
                    {
                        ["_id"] = i,
                        // NOTE: `host` represents the host and port that should be accessible from within the MongoDB server's container.
                        ["host"] = $"{m.Member.Name}:{m.Member.PrimaryEndpoint.TargetPort!.Value}", // NOTE: We know this is always set.
                        // NOTE: `horizons` is a poorly-documented but quite essential MongoDB feature when it comes to clustering — see https://github.com/mongodb/mongo/tree/master/src/mongo/db/repl/split_horizon as well as https://www.percona.com/blog/using-replicasethorizons-in-mongodb/
                        ["horizons"] = new BsonDocument
                        {
                            // NOTE: This represents the host and port that would actually be advertised to outside clients, and should as such be accessible from outside the MongoDB server's container
                            // NOTE: The property name (`external`) here is merely information, what matters is the value and specifically whether or not the hostname in the value matches the SNI of the incoming client connections.
                            ["external"] = await m.Member.PrimaryEndpoint
                                .Property(EndpointProperty.HostAndPort)
                                .GetValueAsync(ct)
                                .ConfigureAwait(false),
                        }
                    })).ConfigureAwait(false)
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
                var v = await foo.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { ["hello"] = 1 }, cancellationToken: ct).ConfigureAwait(false);

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
        member
            .WithReplicaSet(builder.Resource.Name)
            .WithTls() // NOTE: TLS is actually necessary here, because the `horizons` feature used for initializing the replica set operates on top of SNI, which requires client-to-server TLS to be enabled.
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
