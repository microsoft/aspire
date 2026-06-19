// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class PersistentContainerEndToEndTests(ITestOutputHelper output)
{
    private const string ProjectName = "PersistenceE2E";

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/17995")]
    [CaptureWorkspaceOnFailure]
    public async Task PersistentContainersPreserveDataAcrossAppHostRuns()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        // The AppHost runs inside the E2E container while DCP starts backing containers through the host Docker socket.
        // Host networking lets project resources connect to the Docker-published ports that Aspire puts in connection strings.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace, network: "host");
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var appHostCode = $$"""
            #pragma warning disable ASPIREPERSISTENCE001

            var builder = DistributedApplication.CreateBuilder(args);

            var redis = builder.AddRedis("redis")
                .WithPersistentLifetime();

            var postgres = builder.AddPostgres("postgres")
                .WithPersistentLifetime();
            var postgresDatabase = postgres.AddDatabase("pgdb");

            var storage = builder.AddAzureStorage("storage")
                .RunAsEmulator(container => container.WithPersistentLifetime());
            var blobs = storage.AddBlobs("blobs");

            builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                .WithReference(redis)
                .WithReference(postgresDatabase)
                .WithReference(blobs)
                .WaitFor(redis)
                .WaitFor(postgresDatabase)
                .WaitFor(blobs)
                .WithExternalHttpEndpoints();

            builder.Build().Run();
            """;

        var apiProgramCode = """
            using Azure.Storage.Blobs;
            using Npgsql;
            using StackExchange.Redis;

            const string Marker = "persistent-container-value";
            const string RedisKey = "persistent-container-key";

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            builder.AddRedisClient("redis");
            builder.AddNpgsqlDataSource("pgdb");
            builder.AddAzureBlobServiceClient("blobs");

            var app = builder.Build();
            app.MapDefaultEndpoints();

            app.MapGet("/write", async (IConnectionMultiplexer redis, NpgsqlDataSource postgres, BlobServiceClient blobService) =>
            {
                await redis.GetDatabase().StringSetAsync(RedisKey, Marker);

                await using (var connection = await postgres.OpenConnectionAsync())
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS persistence_check (id integer PRIMARY KEY, value text NOT NULL);" +
                        "INSERT INTO persistence_check (id, value) VALUES (1, 'persistent-container-value') " +
                        "ON CONFLICT (id) DO UPDATE SET value = EXCLUDED.value;";
                    await command.ExecuteNonQueryAsync();
                }

                var container = blobService.GetBlobContainerClient("persistence-check");
                await container.CreateIfNotExistsAsync();
                await container.GetBlobClient("marker").UploadAsync(BinaryData.FromString(Marker), overwrite: true);

                return Results.Ok("PERSISTENCE_WRITE_OK");
            });

            app.MapGet("/verify", async (IConnectionMultiplexer redis, NpgsqlDataSource postgres, BlobServiceClient blobService) =>
            {
                var redisValue = await redis.GetDatabase().StringGetAsync(RedisKey);
                if (redisValue != Marker)
                {
                    return Results.Problem($"Redis value was '{redisValue}'.");
                }

                await using (var connection = await postgres.OpenConnectionAsync())
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT value FROM persistence_check WHERE id = 1";
                    var postgresValue = (await command.ExecuteScalarAsync())?.ToString();
                    if (!StringComparer.Ordinal.Equals(postgresValue, Marker))
                    {
                        return Results.Problem($"PostgreSQL value was '{postgresValue}'.");
                    }
                }

                var blob = blobService.GetBlobContainerClient("persistence-check").GetBlobClient("marker");
                var blobContent = (await blob.DownloadContentAsync()).Value.Content.ToString();
                if (blobContent != Marker)
                {
                    return Results.Problem($"Azure Storage blob value was '{blobContent}'.");
                }

                return Results.Ok("PERSISTENCE_VERIFY_OK");
            });

            app.Run();
            """;

        await auto.ScaffoldK8sDeployProjectAsync(
            counter,
            ProjectName,
            Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
            appHostHostingPackages: ["Aspire.Hosting.Redis", "Aspire.Hosting.PostgreSQL", "Aspire.Hosting.Azure.Storage"],
            apiClientPackages: ["Aspire.StackExchange.Redis", "Aspire.Npgsql", "Aspire.Azure.Storage.Blobs"],
            appHostCode: appHostCode,
            apiProgramCode: apiProgramCode,
            output: output);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(5));
        await VerifyEndpointAsync(auto, counter, "/write", "PERSISTENCE_WRITE_OK");
        await auto.AspireStopAsync(counter);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(5));
        await VerifyEndpointAsync(auto, counter, "/verify", "PERSISTENCE_VERIFY_OK");
        await auto.AspireStopAsync(counter);
    }

    /// <summary>
    /// Waits for the server resource and verifies that the endpoint returns the expected marker.
    /// </summary>
    /// <param name="auto">The terminal automator used to run CLI and shell commands.</param>
    /// <param name="counter">The prompt sequence counter used to synchronize command completion.</param>
    /// <param name="path">The server endpoint path to call.</param>
    /// <param name="marker">The marker text expected in the endpoint response.</param>
    private static async Task VerifyEndpointAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string path, string marker)
    {
        await auto.TypeAsync("aspire wait server --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        await auto.TypeAsync("aspire describe server --format json > server.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        await auto.TypeAsync($"SERVER_URL=$(jq -er '.resources[0].urls[0].url' server.json) && for i in $(seq 1 30); do result=$(curl -ksS \"$SERVER_URL{path}\" 2>/dev/null || true); echo \"$result\"; echo \"$result\" | grep -q '{marker}' && break; sleep 2; done && echo \"$result\" | grep -q '{marker}'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }
}
