// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Aspire.Hosting.MongoDB;

internal sealed class MongoDBServerHealthCheck(MongoDBServerResource resource, Func<string?> connectionStringFactory) : IHealthCheck
{
    private IMongoClient? _client;
    private int _replicaSetInitiated; // 0 = not yet initiated, 1 = initiated

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = connectionStringFactory() ?? throw new InvalidOperationException("Connection string is unavailable");
            _client ??= new MongoClient(connectionString);

            if (resource.ReplicaSetName is { } replicaSetName)
            {
                var admin = _client.GetDatabase("admin");

                if (Interlocked.CompareExchange(ref _replicaSetInitiated, 1, 0) == 0)
                {
                    try
                    {
                        var targetPort = resource.PrimaryEndpoint.TargetPort ?? 27017;
                        var initCmd = new BsonDocument("replSetInitiate", new BsonDocument
                        {
                            { "_id", replicaSetName },
                            { "members", new BsonArray { new BsonDocument { { "_id", 0 }, { "host", $"{resource.Name}:{targetPort}" } } } }
                        });

                        await admin.RunCommandAsync<BsonDocument>(initCmd, ReadPreference.Nearest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MongoCommandException ex) when (ex.Code == 23) // AlreadyInitialized
                    {
                        // Replica set already initiated — this is expected on restart or re-check.
                    }
                    catch
                    {
                        // Unexpected failure: reset the flag so the next health check retries initiation.
                        Interlocked.Exchange(ref _replicaSetInitiated, 0);
                        throw;
                    }
                }

                var helloCmd = new BsonDocument("hello", 1);
                var hello = await admin.RunCommandAsync<BsonDocument>(helloCmd, ReadPreference.Nearest, cancellationToken).ConfigureAwait(false);

                return hello.GetValue("isWritablePrimary", false).AsBoolean
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Replica set primary not yet elected.");
            }
            else
            {
                await _client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Healthy();
            }
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
