// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding MongoDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
[Experimental("ASPIREMONGODB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class MongoDBReplicaSetBuilderExtensions
{
    private const int MaxRetriesAttempt = 10;
    // NOTE: MongoDB allows a replica set to hold at most 50 members, at most 7 of which may vote — see https://www.mongodb.com/docs/manual/reference/limits/#mongodb-limit-Number-of-Members-of-a-Replica-Set
    private const int MaxMembers = 50;
    private const int MaxVotingMembers = 7;
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
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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

                    var memberConnections = await Task.WhenAll(membersList.Select(async member => new MemberConnection(
                        member,
                        await member.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                            ?? throw new DistributedApplicationException($"The connection string of MongoDB replica set member '{member.Name}' could not be resolved.")
                    ))).ConfigureAwait(false);
                    var initialPrimary = memberConnections[0];

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
                    var lastConfigurationError = null as MongoCommandException;
                    for (var retries = 0; retries < MaxRetriesAttempt; retries++)
                    {
                        var allMembersUninitialized = true;
                        // NOTE: Every member is probed rather than just the first one that answers, so that the newest
                        // configuration is the one that gets extended. Reconfiguring from a member that had fallen behind
                        // would push its stale view back over the rest, which a forced reconfiguration will not stop.
                        (MongoDBReplicaSetBuilderExtensions.MemberConnection Connection, BsonDocument Config, int Version)? newest = null;
                        foreach (var memberConnection in memberConnections)
                        {
                            using var memberClient = new MongoClient(memberConnection.ConnectionString);
                            var admin = memberClient.GetDatabase("admin");

                            try
                            {
                                logger.LogInformation("Retrieving MongoDB replica set information ({ResourceName}) from member '{MemberName}'", resource.Name, memberConnection.Resource.Name);
                                var currentConfig = await admin.RunCommandAsync<BsonDocument>(
                                    command: new BsonDocument
                                    {
                                        ["replSetGetConfig"] = 1,
                                    },
                                    // NOTE: `RunCommandAsync` selects a server with `ReadPreference.Primary` unless it is
                                    // told otherwise, rather than inheriting the preference from the connection string. A
                                    // member that holds the persisted configuration may well have no primary yet, which is
                                    // the very situation this is trying to recover from, so asking for one would time out
                                    // instead of finding the configuration.
                                    readPreference: ReadPreference.Nearest,
                                    cancellationToken: ct
                                ).ConfigureAwait(false);

                                var version = currentConfig["config"]["version"].AsInt32;
                                if (newest is null || version > newest.Value.Version)
                                {
                                    newest = (memberConnection, currentConfig, version);
                                }

                                allMembersUninitialized = false;
                            }
                            catch (Exception ex) when (ex is TimeoutException or MongoConnectionException)
                            {
                                // NOTE: Waiting for a member to start does not guarantee that `mongod` inside it is already
                                // accepting connections. An unreachable member might still own the persisted configuration,
                                // so initialization is unsafe until every member has positively reported `NotYetInitialized`.
                                allMembersUninitialized = false;
                                logger.LogInformation("MongoDB replica set member '{MemberName}' is not accepting connections yet", memberConnection.Resource.Name);
                            }
                            catch (MongoCommandException ex) when (ex.CodeName is ReplicaSetNotYetInitializedCodeName)
                            {
                                // Keep probing. A newly inserted member can be uninitialized while another declared member
                                // still holds the persisted replica set configuration that must be preserved.
                            }
                        }

                        if (newest is { } existing)
                        {
                            using var existingClient = new MongoClient(existing.Connection.ConnectionString);
                            var admin = existingClient.GetDatabase("admin");
                            var currentMembers = existing.Config["config"]["members"].AsBsonArray;

                            try
                            {
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

                                var desiredMembers = BuildMembersConfiguration(memberHosts, currentMembers);

                                // NOTE: A forced reconfiguration skips the checks that a normal one performs, so it is worth
                                // not doing at all when it would change nothing. That is the common case for a set whose
                                // members kept their addresses.
                                if (MembersConfigurationMatches(currentMembers, desiredMembers))
                                {
                                    logger.LogInformation("MongoDB replica set resource '{ResourceName}' is already configured as declared — leaving it as it is", resource.Name);
                                    configured = true;
                                    break;
                                }

                                logger.LogInformation("Re-configuring MongoDB replica set resource '{ResourceName}' from member '{MemberName}' — last version {Version}", resource.Name, existing.Connection.Resource.Name, existing.Version);
                                await admin.RunCommandAsync<BsonDocument>(
                                    command: new BsonDocument
                                    {
                                        ["replSetReconfig"] = new BsonDocument
                                        {
                                            ["_id"] = rsResource.Name,
                                            ["version"] = existing.Version + 1,
                                            ["members"] = desiredMembers,
                                        },
                                        ["force"] = true,
                                    },
                                    // NOTE: A forced reconfiguration exists precisely to be applied from a member that is
                                    // not primary, so it must not ask for one to run it.
                                    readPreference: ReadPreference.Nearest,
                                    cancellationToken: ct
                                ).ConfigureAwait(false);
                                configured = true;
                                break;
                            }
                            catch (MongoCommandException ex) when (ex.CodeName is NewReplicaSetConfigurationIncompatibleCodeName or ConfigurationInProgressCodeName)
                            {
                                // NOTE: `ConfigurationInProgress` is always transient. `NewReplicaSetConfigurationIncompatible`
                                // is returned both for a version that another update has already moved past, which a retry
                                // resolves, and for a configuration MongoDB will never accept, which no number of retries
                                // will. The two are indistinguishable from the code alone, so it is retried and the server's
                                // own explanation is kept to be reported if the retries run out.
                                lastConfigurationError = ex;
                                logger.LogInformation("Reconfiguring the replica set failed: {Reason}", ex.Message);
                            }
                        }

                        if (configured)
                        {
                            break;
                        }

                        if (allMembersUninitialized)
                        {
                            using var primaryClient = new MongoClient(initialPrimary.ConnectionString);
                            var admin = primaryClient.GetDatabase("admin");
                            try
                            {
                                logger.LogInformation("Initializing MongoDB replica set resource '{ResourceName}'", resource.Name);

                                // NOTE: There is no existing configuration to preserve member ids from, so all of them are freshly allocated.
                                var membersBsonArray = BuildMembersConfiguration(memberHosts, currentMembers: null);

                                // NOTE: The initialization is performed in two steps, first with a single member and then with the full configuration. `replSetInitiate` runs a quorum check against every host in the configuration it is handed and only returns once an election has succeeded, so initiating with the full member list makes success depend on every other member already being reachable. Initiating with only the initial primary elects it immediately, and the remaining members are then added by a reconfiguration, which does not have to wait on them.
                                await admin.RunCommandAsync<BsonDocument>(new BsonDocument
                                {
                                    ["replSetInitiate"] = new BsonDocument
                                    {
                                        ["_id"] = rsResource.Name,
                                        ["members"] = new BsonArray([membersBsonArray[0]]),
                                    },
                                    // NOTE: A member has no primary until this very command has run, so selecting one is
                                    // the one thing that cannot be asked for here.
                                }, readPreference: ReadPreference.Nearest, cancellationToken: ct).ConfigureAwait(false);

                                await admin.RunCommandAsync<BsonDocument>(new BsonDocument
                                {
                                    ["replSetReconfig"] = new BsonDocument
                                    {
                                        ["_id"] = rsResource.Name,
                                        ["version"] = 2,
                                        ["members"] = membersBsonArray,
                                    },
                                    ["force"] = true,
                                }, readPreference: ReadPreference.Nearest, cancellationToken: ct).ConfigureAwait(false);
                                configured = true;
                                break;
                            }
                            catch (MongoCommandException initiateEx) when (initiateEx.CodeName is ReplicaSetAlreadyInitializedCodeName or NewReplicaSetConfigurationIncompatibleCodeName or ConfigurationInProgressCodeName)
                            {
                                // NOTE: Happens when in race with another concurrent process trying to initialize the replica set; so we retry the whole operation
                                lastConfigurationError = initiateEx;
                                logger.LogInformation("Initiating the replica set failed: {Reason}", initiateEx.Message);
                            }
                            catch (Exception ex) when (ex is TimeoutException or MongoConnectionException)
                            {
                                logger.LogInformation("MongoDB replica set member '{MemberName}' stopped accepting connections before initialization completed", initialPrimary.Resource.Name);
                            }
                        }

                        if (!configured && retries < MaxRetriesAttempt - 1)
                        {
                            logger.LogInformation("MongoDB replica set configuration retry attempt {Current}/{Max} will begin after {WaitIntervalSeconds} seconds", retries + 1, MaxRetriesAttempt, s_rsInitiationRetryWaitInterval.TotalSeconds);
                            await Task.Delay(s_rsInitiationRetryWaitInterval, ct).ConfigureAwait(false);
                        }
                    }

                    if (!configured)
                    {
                        // NOTE: Every attempt ran into a retryable error. The replica set is at best partially configured at
                        // this point, so it must not be reported as running.
                        // NOTE: The last error MongoDB gave is carried into the message, because a configuration it will
                        // never accept fails exactly like one that simply lost a race, and only the server's own
                        // explanation tells the two apart.
                        throw new DistributedApplicationException(
                            $"Failed to configure MongoDB replica set resource '{resource.Name}' after {MaxRetriesAttempt} attempts.{(lastConfigurationError is null ? "" : $" The last error reported by MongoDB was: {lastConfigurationError.Message}")}",
                            lastConfigurationError!);
                    }

                    rsResource.IsConfigured = true;
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

        if (builder.Resource.Members.Count() >= MaxMembers)
        {
            throw new InvalidOperationException($"The MongoDB replica set '{builder.Resource.Name}' already has the maximum of {MaxMembers} members that MongoDB allows a replica set to hold.");
        }

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

        // NOTE: Members authenticate to each other with the replica set's own shared key file, so a member that has been
        // given one of its own is a conflict rather than something to quietly overwrite. This is checked here, before
        // anything is mutated, so that the rejected member is left exactly as it was.
        var memberKeyFile = member.Resource.TryGetLastAnnotation<MongoDBServerKeyFileAnnotation>(out var existingKeyFile) ? existingKeyFile : null;
        if (memberKeyFile is not null && memberKeyFile.Value != builder.Resource.SharedKeyFileParameter)
        {
            throw new InvalidOperationException(
                $"The MongoDB server resource '{member.Resource.Name}' was given a key file of its own, which conflicts with the one shared by the members of the replica set '{builder.Resource.Name}'. Members are given the replica set's key file automatically, so remove the '{nameof(MongoDBBuilderExtensions.WithKeyFile)}' call on the member.");
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

        member.WithReplicaSet(builder.Resource.Name);

        // NOTE: A member that already carries the replica set's shared key file keeps it exactly as it is, mounted wherever
        // the caller put it. Configuring it again would only be a no-op when the path happened to match the default too,
        // and would otherwise throw after the line above had already mutated the member.
        if (memberKeyFile is null)
        {
            member.WithKeyFile(builder.Resource.SharedKeyFileParameter);
        }

        member
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

        member.Resource.UserNameParameter = builder.Resource.SharedUserNameParameter;
        member.Resource.PasswordParameter = builder.Resource.SharedPasswordParameter;
        member.Resource.PasswordParameterWasGenerated = false;

        // NOTE: The replica set has no process of its own, so the orchestrator cannot move it out of `Running` when its
        // members go away and it would otherwise claim to be running long after everything backing it had stopped. Its
        // members' lifecycle is followed instead: it is only up while at least one of them is.
        member.OnResourceStopped(async (stopped, evt, ct) =>
        {
            await evt.Services.GetRequiredService<ResourceNotificationService>()
                .PublishUpdateAsync(builder.Resource, s =>
                {
                    lock (builder.Resource.StoppedMembers)
                    {
                        builder.Resource.StoppedMembers.Add(stopped.Name);
                        return builder.Resource.StoppedMembers.Count >= builder.Resource.Members.Count()
                            ? s with { State = KnownResourceStates.Exited }
                            : s;
                    }
                })
                .ConfigureAwait(false);
        });

        member.OnBeforeResourceStarted(async (starting, evt, ct) =>
        {
            await evt.Services.GetRequiredService<ResourceNotificationService>()
                .PublishUpdateAsync(builder.Resource, s =>
                {
                    lock (builder.Resource.StoppedMembers)
                    {
                        var wasAllStopped = builder.Resource.StoppedMembers.Count >= builder.Resource.Members.Count()
                            && builder.Resource.StoppedMembers.Count > 0;
                        builder.Resource.StoppedMembers.Remove(starting.Name);

                        // NOTE: The configuration lives in the members' own data, so a set that has already been configured
                        // does not need to be initialized again. The member is not ready yet, however, so the set must remain
                        // `Starting` until its health check succeeds.
                        return wasAllStopped && builder.Resource.IsConfigured
                            ? s with { State = KnownResourceStates.Starting }
                            : s;
                    }
                })
                .ConfigureAwait(false);
        });

        member.OnResourceReady(async (ready, evt, ct) =>
        {
            await evt.Services.GetRequiredService<ResourceNotificationService>()
                .PublishUpdateAsync(builder.Resource, s =>
                {
                    lock (builder.Resource.StoppedMembers)
                    {
                        return !ct.IsCancellationRequested
                            && builder.Resource.IsConfigured
                            && !builder.Resource.StoppedMembers.Contains(ready.Name)
                                ? s with { State = KnownResourceStates.Running }
                                : s;
                    }
                })
                .ConfigureAwait(false);
        });

        return builder
            .WithAnnotation(new MongoReplicaSetMemberAnnotation(member.Resource))
            // NOTE: This deliberately waits for the member to start rather than to become healthy. A member that carries
            // `--replSet` has no primary until `replSetInitiate` has run against it, and anything that asks it for one
            // cannot succeed before then — so waiting for health here would be waiting for something that this very
            // resource is responsible for bringing about. Waiting for the container to be running is all the initialization
            // below actually needs, since that is what makes the member's endpoint resolvable.
            .WaitForStart(member)
            .WithRelationship(member, "replica set member");
    }

    /// <summary>
    /// Whether a replica set's current <c>members</c> array already says what <paramref name="desired"/> says, so that a
    /// reconfiguration would change nothing.
    /// </summary>
    /// <remarks>
    /// The current configuration carries defaults that MongoDB filled in and that we never set, so only the fields we do
    /// set are compared. Anything else would report a difference on every startup and defeat the point.
    /// </remarks>
    private static bool MembersConfigurationMatches(BsonArray current, BsonArray desired)
    {
        if (current.Count != desired.Count)
        {
            return false;
        }

        var currentById = current.OfType<BsonDocument>().ToDictionary(m => m["_id"].AsInt32);
        foreach (var desiredMember in desired.OfType<BsonDocument>())
        {
            if (!currentById.TryGetValue(desiredMember["_id"].AsInt32, out var currentMember))
            {
                return false;
            }

            foreach (var field in desiredMember.Names)
            {
                if (!currentMember.TryGetValue(field, out var currentValue) || currentValue != desiredMember[field])
                {
                    return false;
                }
            }

            // NOTE: A member that votes carries no `votes` or `priority` of its own, since those are MongoDB's defaults, so
            // the absence of them has to be checked against the current values rather than skipped. Otherwise a member that
            // stopped being the eighth one and should vote again would look unchanged and quietly stay non-voting.
            if (!desiredMember.Contains("votes") && currentMember.GetValue("votes", 1).ToInt32() != 1)
            {
                return false;
            }

            if (!desiredMember.Contains("priority") && currentMember.GetValue("priority", 1).ToDouble() != 1)
            {
                return false;
            }
        }

        return true;
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

            var document = new BsonDocument
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
            };

            // NOTE: A replica set may hold no more than seven voting members, and `replSetInitiate` fails outright if it is
            // handed more, so members past the seventh join as non-voting ones. They still carry a full copy of the data and
            // can serve reads; they just take no part in elections.
            if (result.Count >= MaxVotingMembers)
            {
                document["votes"] = 0;
                document["priority"] = 0;
            }

            result.Add(document);
        }

        return result;
    }

    /// <summary>
    /// The addresses a replica set member is reachable at, from within the container network (<paramref name="Internal"/>)
    /// and from outside of it (<paramref name="External"/>).
    /// </summary>
    internal readonly record struct MemberHosts(string Internal, string External);

    internal readonly record struct MemberConnection(MongoDBServerResource Resource, string ConnectionString);
}

internal sealed record MongoReplicaSetMemberAnnotation(
    MongoDBServerResource Member
) : IResourceAnnotation;
