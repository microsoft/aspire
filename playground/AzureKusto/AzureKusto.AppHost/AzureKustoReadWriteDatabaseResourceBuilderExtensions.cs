// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;

internal static class AzureKustoReadWriteDatabaseResourceBuilderExtensions
{
    private static readonly ResiliencePipeline s_resiliencePipeline = new ResiliencePipelineBuilder()
        .AddRetry(new()
        {
            // Retry any non-permanent exceptions
            MaxRetryAttempts = 10,
            Delay = TimeSpan.FromMilliseconds(100),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is ICloudPlatformException cpe && !cpe.IsPermanent),
        })
        .Build();

    [AspireExportIgnore(Reason = "Kusto playground helper that registers .NET callbacks for emulator control commands; not intended for ATS export.")]
    public static IResourceBuilder<AzureKustoReadWriteDatabaseResource> WithControlCommand(this IResourceBuilder<AzureKustoReadWriteDatabaseResource> dbBuilder, string command)
    {
        dbBuilder.OnResourceReady(async (dbResource, evt, ct) =>
        {
            if (!dbBuilder.Resource.Parent.IsEmulator)
            {
                var logger = evt.Services.GetRequiredService<ResourceLoggerService>().GetLogger(dbBuilder.Resource);
                logger.LogInformation("Skipping Kusto DB control command for non-emulator.");
                return;
            }

            var connectionString = await dbResource.ConnectionStringExpression.GetValueAsync(ct);
            var kcsb = new KustoConnectionStringBuilder(connectionString);

            using var admin = KustoClientFactory.CreateCslAdminProvider(kcsb);

            await s_resiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                return await admin.ExecuteControlCommandAsync(admin.DefaultDatabaseName, command);
            },
            ct);
        });

        return dbBuilder;
    }
}
