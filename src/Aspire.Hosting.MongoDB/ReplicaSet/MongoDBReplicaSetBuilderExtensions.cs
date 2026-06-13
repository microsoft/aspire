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

        var replicaSet = new MongoDBReplicaSetResource(
            name: name,
            keyFile: ParameterResourceBuilderExtensions.CreateGeneratedParameter(
                builder,
                $"{name}-keyfile-content",
                secret: true,
                new GenerateParameterDefault
                {
                    MinLength = 32,
                    Special = false,
                }
            )
        );

        builder.Eventing.Subscribe<ResourceReadyEvent>(replicaSet, async (@event, ct) =>
        {
            if (!replicaSet.TryGetAnnotationsOfType<MongoReplicaSetMemberAnnotation>(out var members) || !members.Any())
            {
                return;
            }

            var initialPrimary = members.First();
            var connectionStringToPrimary = await initialPrimary.Member.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            var primaryClient = new MongoClient(connectionStringToPrimary);

            var membersBsonArray = new BsonArray(
                await Task.WhenAll(members.Select(async (m, i) => new BsonDocument
                {
                    ["_id"] = i,
                    ["host"] = $"{m.Member.Name}:{await m.Member.PrimaryEndpoint.Property(EndpointProperty.HostAndPort).GetValueAsync(ct).ConfigureAwait(false)}"
                })).ConfigureAwait(false)
            );

            var admin = primaryClient.GetDatabase("admin");
            while (true)
            {
                try
                {
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
                            ["_id"] = replicaSet.Name,
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
            }
        });

        string? connectionString = null;
        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(replicaSet, async (@event, ct) =>
        {
            connectionString = await replicaSet.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
            .AddMongoDb(
                sp => new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is not yet available")),
                name: healthCheckKey);

        return builder
            .AddResource(replicaSet)
            .WithHealthCheck(healthCheckKey);
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
