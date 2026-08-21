// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding MongoDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class MongoDBReplicaSetBuilderExtensions
{
    private const int MaxRetriesAttempt = 10;
    private const string ReplicaSetAlreadyInitializedCodeName = "AlreadyInitialized";
    private const string ReplicaSetNotYetInitializedCodeName = "NotYetInitialized";
    private const string NewReplicaSetConfigurationIncompatibleCodeName = "NewReplicaSetConfigurationIncompatible";
    private const string ConfigurationInProgressCodeName = "ConfigurationInProgress"; // NOTE: Represents the error `Cannot run replSetReconfig because the node is currently updating its configuration.` that can be returned by `replSetReconfig` when a preceding `replSetInitiate` (or `replSetReconfig`, for that matter) command is still being processed in the background.
    private static readonly TimeSpan s_rsInitiationRetryWaitInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Adds a MongoDB replica set resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to which the replica set resource will be added.</param>
    /// <param name="name">The name of the replica set resource.</param>
    /// <param name="userName">An optional parameter resource that contains the username for authenticating to the MongoDB replica set. If not provided, a default username will be used.</param>
    /// <param name="password">An optional parameter resource that contains the password for authenticating to the MongoDB replica set. If not provided, a default password will be used.</param>
    /// <remarks>
    /// <para>
    /// This is a "logical" resource that groups multiple <see cref="MongoDBServerResource"/> instances that are annotated as members of the replica set.
    /// </para>
    /// <para>
    /// The replica set is initialized by the app host itself, which is something that only happens when running locally.
    /// Publishing and deploying an application that contains a MongoDB replica set is therefore not supported yet and
    /// this method throws when the app host runs in publish mode.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var mongo1 = builder.AddMongoDB("mongo-1");
    /// var mongo2 = builder.AddMongoDB("mongo-2");
    ///
    /// var replicaSet = builder.AddMongoDBReplicaSet("rs0")
    ///     .WithMember(mongo1)
    ///     .WithMember(mongo2);
    /// </code>
    /// </example>
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
        // NOTE: The replica set is only ever initialized by the local orchestration callback below. A published deployment
        // would start the member containers with `--replSet` but never run `replSetInitiate`/`replSetReconfig` against
        // them, leaving the advertised connection string unusable, so publishing is rejected outright for now.
        MongoDBBuilderExtensions.ThrowIfPublishMode(builder, nameof(AddMongoDBReplicaSet));

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

        // NOTE: `clientFactory` is invoked every time the healthcheck is performed. We cache the client so it is reused.
        var client = null as IMongoClient;
        builder.Services.AddHealthChecks()
            .AddMongoDb(
                sp => client ??= new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable")),
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
                // NOTE: `evt.Logger` is backed by `ResourceLoggerService` for this resource, so what is logged here shows up
                // in this resource's console in the dashboard. A category logger would only reach the app host log, which is
                // the wrong place for diagnostics about why this resource failed to start.
                var logger = evt.Logger;

                try
                {
                    var membersList = rsResource.Members.ToList();
                    if (membersList is [])
                    {
                        logger.LogCritical("Cannot initialize MongoDB replica set resource '{ResourceName}' because it does not have any members.", resource.Name);
                        await evt.Notifications.PublishUpdateAsync(resource, s => s with
                        {
                            State = KnownResourceStates.FailedToStart,
                        }).ConfigureAwait(false);
                        return;
                    }

                    // NOTE: This is where waiting happens. `WithMember` adds a `WaitFor` annotation for each member, but
                    // those annotations are only honored by whoever publishes `BeforeResourceStartedEvent`; without this,
                    // resolving the endpoints below and connecting to the initial primary would race member startup.
                    await evt.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, evt.Services), ct)
                        .ConfigureAwait(false);

                    connectionString = await rsResource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                    await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(resource, evt.Services), ct)
                        .ConfigureAwait(false);

                    await evt.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.Starting,
                    }).ConfigureAwait(false);

                    if (membersList.Find(m => !m.TlsEnabled) is { } memberWithoutTls)
                    {
                        // NOTE: TLS is not optional for a replica set here: the `horizons` mechanism used below to advertise
                        // host-reachable addresses to outside clients keys off the SNI of the incoming connection, which
                        // only exists on TLS connections.
                        throw new DistributedApplicationException($"MongoDB replica set member '{memberWithoutTls.Name}' does not have TLS enabled, which is required for members of a replica set. Ensure an HTTPS/TLS certificate is available for the member, for example by trusting the ASP.NET Core developer certificate.");
                    }

                    var initialPrimary = membersList[0];
                    var connectionStringToPrimary = await initialPrimary.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                    var memberHosts = await Task.WhenAll(membersList.Select(async m => new MemberHosts(
                        // NOTE: `Internal` represents the host and port that should be accessible from within the MongoDB server's container.
                        // NOTE: We know that the `TargetPort` always has a value (of 27017).
                        Internal: $"{m.Name}:{m.PrimaryEndpoint.TargetPort!.Value}",
                        // NOTE: `External` represents the host and port that would actually be advertised to outside clients, and should as such be accessible from outside the MongoDB server's container.
                        External: await m.PrimaryEndpoint
                            .Property(EndpointProperty.HostAndPort)
                            .GetValueAsync(ct)
                            .ConfigureAwait(false) ?? throw new DistributedApplicationException($"The endpoint of MongoDB replica set member '{m.Name}' could not be resolved.")
                    ))).ConfigureAwait(false);

                    var configured = false;
                    for (var retries = 0; retries < MaxRetriesAttempt; retries++)
                    {
                        using var primaryClient = new MongoClient(connectionStringToPrimary);
                        var admin = primaryClient.GetDatabase("admin");

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
                            var currentMembers = currentConfig["config"]["members"].AsBsonArray;

                            // NOTE: A forced reconfiguration that both drops a member and moves the remaining members' split
                            // horizons — which is what restarting the app host does, since the host ports are reassigned —
                            // leaves the surviving members unable to pick the new configuration up from each other, so the
                            // replica set never elects a primary again. Rather than reconfiguring a persisted set into that
                            // state, the removal is refused and the set is left on its current configuration.
                            var removedHosts = currentMembers
                                .OfType<BsonDocument>()
                                .Select(m => m["host"].AsString)
                                .Except(memberHosts.Select(m => m.Internal), StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (removedHosts.Count > 0)
                            {
                                throw new DistributedApplicationException($"Cannot remove {string.Join(", ", removedHosts.Select(h => $"'{h}'"))} from the existing MongoDB replica set '{rsResource.Name}': removing members from a replica set that has already been initialized is not supported yet. Add the member(s) back, or start over from an empty replica set by removing the data volumes of its members.");
                            }

                            logger.LogInformation("Re-configuring MongoDB replica set resource '{ResourceName}' — last version {Version}", resource.Name, version);
                            await admin.RunCommandAsync<BsonDocument>(
                                command: new BsonDocument
                                {
                                    ["replSetReconfig"] = new BsonDocument
                                    {
                                        ["_id"] = rsResource.Name,
                                        ["version"] = version + 1,
                                        ["members"] = BuildMembersConfiguration(memberHosts, currentMembers),
                                    },
                                    ["force"] = true,
                                },
                                cancellationToken: ct
                            ).ConfigureAwait(false);
                            configured = true;
                            break;
                        }
                        catch (MongoCommandException ex) when (ex.CodeName is NewReplicaSetConfigurationIncompatibleCodeName or ConfigurationInProgressCodeName)
                        {
                            // NOTE: Happens when another concurrent process has already updated the replica set configuration with a higher version, or when a preceding configuration update is still being applied. Either way we need to re-fetch the current configuration and retry with an updated version number.
                            logger.LogInformation("Reconfiguring the replica set failed because another configuration update is in flight — retry attempt {Current}/{Max} to begin after {WaitIntervalSeconds} seconds", retries, MaxRetriesAttempt, s_rsInitiationRetryWaitInterval.TotalSeconds);
                            await Task.Delay(s_rsInitiationRetryWaitInterval, ct).ConfigureAwait(false);
                        }
                        catch (MongoCommandException ex) when (ex.CodeName is ReplicaSetNotYetInitializedCodeName)
                        {
                            logger.LogInformation("Initializing MongoDB replica set resource '{ResourceName}'", resource.Name);

                            // NOTE: There is no existing configuration to preserve member ids from, so all of them are freshly allocated.
                            var membersBsonArray = BuildMembersConfiguration(memberHosts, currentMembers: null);

                            try
                            {
                                // NOTE: The initialization is performed in two steps, first with a single member and then with the full configuration. `replSetInitiate` runs a quorum check against every host in the configuration it is handed and only returns once an election has succeeded, so initiating with the full member list makes success depend on every other member already being reachable. Initiating with only the initial primary elects it immediately, and the remaining members are then added by a reconfiguration, which does not have to wait on them.
                                await admin.RunCommandAsync<BsonDocument>(new BsonDocument
                                {
                                    ["replSetInitiate"] = new BsonDocument
                                    {
                                        ["_id"] = rsResource.Name,
                                        ["members"] = new BsonArray([membersBsonArray[0]]),
                                    },
                                }, cancellationToken: ct).ConfigureAwait(false);

                                await admin.RunCommandAsync<BsonDocument>(new BsonDocument
                                {
                                    ["replSetReconfig"] = new BsonDocument
                                    {
                                        ["_id"] = rsResource.Name,
                                        ["version"] = 2,
                                        ["members"] = membersBsonArray,
                                    },
                                    ["force"] = true,
                                }, cancellationToken: ct).ConfigureAwait(false);
                                configured = true;
                                break;
                            }
                            catch (MongoCommandException initiateEx) when (initiateEx.CodeName is ReplicaSetAlreadyInitializedCodeName or NewReplicaSetConfigurationIncompatibleCodeName or ConfigurationInProgressCodeName)
                            {
                                // NOTE: Happens when in race with another concurrent process trying to initialize the replica set; so we retry the whole operation
                                logger.LogInformation("Initiating the replica set failed due to it already being initialized — retry attempt {Current}/{Max} to begin after {WaitIntervalSeconds} seconds", retries, MaxRetriesAttempt, s_rsInitiationRetryWaitInterval.TotalSeconds);
                                await Task.Delay(s_rsInitiationRetryWaitInterval, ct).ConfigureAwait(false);
                            }
                        }
                    }

                    if (!configured)
                    {
                        // NOTE: Every attempt ran into a retryable error. The replica set is at best partially configured at
                        // this point, so it must not be reported as running.
                        throw new DistributedApplicationException($"Failed to configure MongoDB replica set resource '{resource.Name}' after {MaxRetriesAttempt} attempts.");
                    }

                    await evt.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.Running,
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Failed to initialize MongoDB replica set resource '{ResourceName}'", resource.Name);
                    await evt.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.FailedToStart,
                    }).ConfigureAwait(false);
                }
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
    /// Internally calls the following methods on the member's builder:
    /// <list type="number">
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithReplicaSet(IResourceBuilder{MongoDBServerResource}, string)"/> to set the replica set name on the member resource and configure it accordingly. </description></item>
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithKeyFile(IResourceBuilder{MongoDBServerResource}, IExpressionValue, string)"/> to set the key file parameter on the member resource, which is required for internal authentication between replica set members. </description></item>
    /// <item> <description><see cref="MongoDBBuilderExtensions.WithTlsAllowInvalidCertificates(IResourceBuilder{MongoDBServerResource})"/> because members authenticate to each other with the same certificate they serve to clients, which does not carry a <c>clientAuth</c> extended key usage. </description></item>
    /// <item> <description><see cref="ResourceBuilderExtensions.WithHttpsDeveloperCertificate{TResource}(IResourceBuilder{TResource}, IResourceBuilder{ParameterResource}?)"/>, unless the member already has certificate configuration of its own. TLS is required for members of a replica set, because the split-horizon member hostname advertisement performed by the server operates on top of SNI. </description></item>
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

        // NOTE: A MongoDB server can only belong to one replica set, and can only appear in it once. Without this check the
        // member would silently accumulate a second set of `--replSet`, key file and bind arguments, or contribute a
        // duplicate host to the replica set configuration, and the failure would only surface when the container starts.
        if (member.Resource.ReplicaSetName is { } existingReplicaSetName)
        {
            throw new InvalidOperationException(
                string.Equals(existingReplicaSetName, builder.Resource.Name, StringComparisons.ResourceName)
                    ? $"The MongoDB server resource '{member.Resource.Name}' has already been added as a member of the replica set '{builder.Resource.Name}'."
                    : $"The MongoDB server resource '{member.Resource.Name}' is already a member of the replica set '{existingReplicaSetName}' and cannot also be a member of '{builder.Resource.Name}'.");
        }

        member
            .WithReplicaSet(builder.Resource.Name)
            .WithKeyFile(builder.Resource.SharedKeyFileParameter)
            // NOTE: Members of a replica set authenticate to each other over TLS using the very certificate they serve to
            // clients, and that certificate does not carry a `clientAuth` extended key usage, so peer validation has to be
            // relaxed for intra-cluster connections to succeed.
            // TODO: Could be removed and replaced with `--tlsClusterFile <file>` (along with the more restrictive `--tlsAllowInvalidHostnames`) once Aspire adds support for TLS certificates with EKUs of `clientAuth` — see https://discord.com/channels/1361488941836140614/1361488942813286403/1516575977256259735
            .WithTlsAllowInvalidCertificates();

        // NOTE: TLS is actually necessary here, because the `horizons` feature used for initializing the replica set
        // operates on top of SNI, which requires client-to-server TLS to be enabled. Members are therefore opted in to the
        // developer certificate explicitly rather than being left to the ambient default — unless the member has been given
        // certificate configuration of its own, which is then honored as-is.
        if (!member.Resource.HasAnnotationOfType<HttpsCertificateAnnotation>())
        {
            member.WithHttpsDeveloperCertificate();
        }

        // NOTE: Every member of a replica set has to authenticate with the same credentials. Even if we don't do this, the
        // primary will propagate its username/password to the other members, but we make sure to model it at the level of
        // the resource graph so that the connection strings to individual members contain the correct credentials when they
        // are used directly (for health checks, for example).
        // NOTE: Credentials the caller chose are never silently replaced. Doing so would not only surprise them, it would
        // break a member with an existing data volume: MongoDB's initialization environment variables only take effect on
        // the very first run, so the volume would keep the credentials it was created with while this run advertised the
        // replica set's, and authentication would fail.
        if (member.Resource.UserNameParameter is { } memberUserName && memberUserName != builder.Resource.SharedUserNameParameter)
        {
            throw new InvalidOperationException(
                $"The MongoDB server resource '{member.Resource.Name}' was given an explicit user name that differs from the one of the replica set '{builder.Resource.Name}'. Members of a replica set share a single set of credentials: pass the user name to '{nameof(AddMongoDBReplicaSet)}' instead of to the individual members.");
        }

        if (!member.Resource.PasswordParameterWasGenerated && member.Resource.PasswordParameter != builder.Resource.SharedPasswordParameter)
        {
            throw new InvalidOperationException(
                $"The MongoDB server resource '{member.Resource.Name}' was given an explicit password that differs from the one of the replica set '{builder.Resource.Name}'. Members of a replica set share a single set of credentials: pass the password to '{nameof(AddMongoDBReplicaSet)}' instead of to the individual members.");
        }

        member.Resource.UserNameParameter = builder.Resource.SharedUserNameParameter;
        member.Resource.PasswordParameter = builder.Resource.SharedPasswordParameter;
        member.Resource.PasswordParameterWasGenerated = false;

        return builder
            .WithAnnotation(new MongoReplicaSetMemberAnnotation(member.Resource))
            .WaitFor(member)
            .WithRelationship(member, "replica set member");
    }

    /// <summary>
    /// Builds the <c>members</c> array of a replica set configuration for <paramref name="members"/>, preserving the
    /// <c>_id</c> of every host that is already part of <paramref name="currentMembers"/>.
    /// </summary>
    /// <remarks>
    /// MongoDB rejects a reconfiguration that assigns a different <c>_id</c> to a host that is already configured, so
    /// member ids cannot simply be the position of the member in the app host's list: removing or reordering members would
    /// then shift the ids of the members that stayed. Existing ids are therefore carried over by host and only genuinely
    /// new members get a freshly allocated, previously unused id.
    /// </remarks>
    internal static BsonArray BuildMembersConfiguration(IReadOnlyList<MemberHosts> members, BsonArray? currentMembers)
    {
        var idsByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<int>();

        foreach (var currentMember in currentMembers?.OfType<BsonDocument>() ?? [])
        {
            var id = currentMember["_id"].AsInt32;
            idsByHost[currentMember["host"].AsString] = id;
            usedIds.Add(id);
        }

        var nextUnusedId = 0;
        var result = new BsonArray();
        foreach (var member in members)
        {
            if (!idsByHost.TryGetValue(member.Internal, out var id))
            {
                while (!usedIds.Add(nextUnusedId))
                {
                    nextUnusedId++;
                }
                id = nextUnusedId;
            }

            result.Add(new BsonDocument
            {
                ["_id"] = id,
                // NOTE: `host` represents the host and port that should be accessible from within the MongoDB server's container.
                ["host"] = member.Internal,
                // NOTE: `horizons` is a poorly-documented but quite essential MongoDB feature when it comes to clustering — see https://github.com/mongodb/mongo/tree/master/src/mongo/db/repl/split_horizon as well as https://www.percona.com/blog/using-replicasethorizons-in-mongodb/
                ["horizons"] = new BsonDocument
                {
                    // NOTE: The property name (`external`) here is purely informational, what matters is the value and specifically whether or not the hostname in the value matches the SNI of the incoming client connections.
                    ["external"] = member.External,
                },
            });
        }

        return result;
    }

    /// <summary>
    /// The addresses a replica set member is reachable at, from within the container network (<paramref name="Internal"/>)
    /// and from outside of it (<paramref name="External"/>).
    /// </summary>
    internal readonly record struct MemberHosts(string Internal, string External);
}

internal sealed record MongoReplicaSetMemberAnnotation(
    MongoDBServerResource Member
) : IResourceAnnotation;
