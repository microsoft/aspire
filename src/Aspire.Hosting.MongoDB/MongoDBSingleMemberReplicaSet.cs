// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.MongoDB;

internal sealed class MongoDBSingleMemberReplicaSetAnnotation(string name) : IResourceAnnotation
{
    public string Name { get; } = name;

    // Initialization and health polling run concurrently. Null means initialization and election succeeded.
    public volatile string? InitializationError = "The single-member replica set has not been initialized.";
}

internal static class MongoDBSingleMemberReplicaSet
{
    internal static async Task InitializeAsync(
        MongoDBServerResource resource,
        MongoDBSingleMemberReplicaSetAnnotation annotation,
        InitializeResourceEvent evt,
        CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, evt.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

        try
        {
            var wasRunning = false;
            // InitializeResourceEvent is dispatched non-blocking, so waiting for Running does not block container
            // startup. ResourceReadyEvent would deadlock: primary election is a prerequisite for this resource's health.
            // Observe start transitions too, so restarting a failed or recreated container retries initialization.
            await foreach (var update in evt.Notifications.WatchAsync(stopping.Token).ConfigureAwait(false))
            {
                if (update.Resource != resource)
                {
                    continue;
                }

                var isRunning = update.Snapshot.State?.Text == KnownResourceStates.Running;
                if (isRunning && !wasRunning)
                {
                    annotation.InitializationError = "Waiting for single-member replica set initialization and primary election.";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));

                    try
                    {
                        var connectionString = await resource.ConnectionStringExpression.GetValueAsync(timeout.Token).ConfigureAwait(false)
                            ?? throw new DistributedApplicationException($"The connection string for MongoDB resource '{resource.Name}' is unavailable.");
                        var settings = MongoClientSettings.FromConnectionString(connectionString);
                        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
                        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
                        using var client = new MongoClient(settings);
                        var database = client.GetDatabase(MongoDBServerResource.DefaultAuthenticationDatabase);

                        // Loopback is stable across container and AppHost restarts, including random host-port changes.
                        // Clients use directConnection=true and never try to discover or connect to this internal address.
                        var host = $"localhost:{resource.PrimaryEndpoint.TargetPort}";
                        await InitializeAndWaitForPrimaryAsync(database, annotation.Name, host, timeout.Token).ConfigureAwait(false);
                        annotation.InitializationError = null;
                        evt.Logger.LogInformation("MongoDB resource '{ResourceName}' is the primary of single-member replica set '{ReplicaSetName}'.", resource.Name, annotation.Name);
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                    {
                        annotation.InitializationError = $"MongoDB resource '{resource.Name}' did not initialize replica set '{annotation.Name}' and elect a primary within 90 seconds. Check the container logs, credentials, and existing data volume, then restart the resource.";
                        evt.Logger.LogError("{Message}", annotation.InitializationError);
                    }
                    catch (Exception ex) when (ex is MongoException or TimeoutException or DistributedApplicationException)
                    {
                        annotation.InitializationError = $"MongoDB resource '{resource.Name}' could not initialize replica set '{annotation.Name}'. Check the container logs and ensure the data volume belongs to this single-member set and uses the configured credentials.";
                        evt.Logger.LogError(ex, "{Message}", annotation.InitializationError);
                    }
                }

                wasRunning = isRunning;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // AppHost shutdown cancels both the notification watch and any in-flight MongoDB command.
        }
    }

    internal static async Task InitializeAndWaitForPrimaryAsync(IMongoDatabase database, string name, string host, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                BsonDocument config;
                try
                {
                    var result = await database.RunCommandAsync<BsonDocument>(
                        new BsonDocument("replSetGetConfig", 1), ReadPreference.Nearest, cancellationToken).ConfigureAwait(false);
                    config = result["config"].AsBsonDocument;
                }
                catch (MongoCommandException ex) when (ex.CodeName == "NotYetInitialized")
                {
                    // The official image creates the authenticated root user using a temporary standalone mongod.
                    // Initiation must target the final server, not an init script against that temporary process.
                    // https://github.com/docker-library/mongo/blob/master/docker-entrypoint.sh
                    await database.RunCommandAsync<BsonDocument>(
                        new BsonDocument("replSetInitiate", new BsonDocument
                        {
                            ["_id"] = name,
                            ["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = host } },
                        }), ReadPreference.Nearest, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ValidateConfiguration(config, name, host);
                var status = await database.RunCommandAsync<BsonDocument>(
                    new BsonDocument("replSetGetStatus", 1), ReadPreference.Nearest, cancellationToken).ConfigureAwait(false);
                if (IsPrimary(status, name))
                {
                    return;
                }
            }
            catch (MongoCommandException ex) when (ex.CodeName == "AlreadyInitialized")
            {
                // Another initializer won the race. Read and validate its configuration; never force a reconfiguration.
            }
            catch (Exception ex) when (ex is MongoConnectionException or TimeoutException)
            {
                // Running means the container started, not that mongod is already accepting authenticated commands.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void ValidateConfiguration(BsonDocument config, string name, string host)
    {
        // replSetGetConfig returns { config: { _id: "mongo", members: [{ _id: 0, host: "localhost:27017", ... }], ... } }.
        // Ignore server-populated defaults but refuse to rewrite existing identities, addresses, or multi-member data.
        if (config["_id"].AsString != name ||
            config["members"].AsBsonArray is not { Count: 1 } members ||
            members[0]["host"].AsString != host)
        {
            throw new DistributedApplicationException($"The existing MongoDB replica set configuration does not match single-member set '{name}' at '{host}'. Use its original configuration and data volume; automatic migration or reconfiguration is not supported.");
        }
    }

    internal static bool IsPrimary(BsonDocument status, string name) =>
        status["set"].AsString == name && status["myState"].AsInt32 == 1 && status["members"].AsBsonArray.Count == 1;
}
