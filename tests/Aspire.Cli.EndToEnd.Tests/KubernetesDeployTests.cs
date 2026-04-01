// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for <c>aspire deploy</c> to Kubernetes via Helm.
/// Tests the interactive deploy workflow: scaffold project, run <c>aspire deploy</c>,
/// answer parameter prompts, verify pod health, and validate via <c>/test-deployment</c> endpoint.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class KubernetesDeployTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployBasicApiService()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare environment
        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            // =====================================================================
            // Phase 1: Install KinD + Helm, create cluster with local registry
            // =====================================================================

            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // =====================================================================
            // Phase 2: Scaffold the project on disk
            // =====================================================================

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", () =>
                {
                    return Results.Ok("PASSED: basic API service is running");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes"],
                apiClientPackages: [],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            // Navigate into the project directory
            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Verify scaffold
            await auto.TypeAsync($"ls -la {ProjectName}.AppHost/");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 3: Unset ASPIRE_PLAYGROUND and run aspire deploy interactively
            // =====================================================================

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The deploy will prompt for:
            // 1. registryendpoint - the container registry (localhost:5001 for KinD local registry)
            // 2. namespace - the K8s namespace
            // 3. chartversion - the Helm chart version
            // Parameters are prompted in alphabetical order by name in a multi-input form.
            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            // =====================================================================
            // Phase 4: Verify the deployment
            // =====================================================================

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18080,
                testPath: "/test-deployment");

            // =====================================================================
            // Phase 5: Cleanup
            // =====================================================================

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithRedis()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // Scaffold: AppHost with Redis + API service that uses Redis client
            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var cache = builder.AddRedis("cache");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(cache)
                    .WaitFor(cache)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using StackExchange.Redis;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddRedisClient("cache");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (IConnectionMultiplexer redis) =>
                {
                    var db = redis.GetDatabase();

                    // Write a value
                    var testKey = $"test-{Guid.NewGuid():N}";
                    await db.StringSetAsync(testKey, "hello-from-k8s");

                    // Read it back
                    var value = await db.StringGetAsync(testKey);

                    // Cleanup
                    await db.KeyDeleteAsync(testKey);

                    if (value == "hello-from-k8s")
                    {
                        return Results.Ok("PASSED: Redis SET+GET works");
                    }
                    return Results.Problem($"FAILED: expected 'hello-from-k8s', got '{value}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Redis"],
                apiClientPackages: ["Aspire.StackExchange.Redis"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Deploy prompts: chartversion, namespace, registryendpoint
            // Redis also generates a password parameter (cache_password)
            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18081,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithPostgres()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var postgres = builder.AddPostgres("pg").AddDatabase("testdb");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(postgres)
                    .WaitFor(postgres)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using Npgsql;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddNpgsqlDataSource("testdb");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (NpgsqlDataSource dataSource) =>
                {
                    await using var conn = await dataSource.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 AS result";
                    var result = await cmd.ExecuteScalarAsync();

                    if (result is int val && val == 1)
                    {
                        return Results.Ok("PASSED: PostgreSQL SELECT 1 works");
                    }
                    return Results.Problem($"FAILED: expected 1, got '{result}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.PostgreSQL"],
                apiClientPackages: ["Aspire.Npgsql"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18082,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithRabbitMQ()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var messaging = builder.AddRabbitMQ("messaging");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(messaging)
                    .WaitFor(messaging)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using RabbitMQ.Client;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddRabbitMQClient("messaging");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", (IConnection connection) =>
                {
                    using var channel = connection.CreateChannel();
                    var queueName = $"test-{Guid.NewGuid():N}";
                    channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: true);
                    channel.QueueDelete(queueName);

                    return Results.Ok("PASSED: RabbitMQ queue declare+delete works");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.RabbitMQ"],
                apiClientPackages: ["Aspire.RabbitMQ.Client"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18083,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithMongoDB()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var mongo = builder.AddMongoDB("mongo").AddDatabase("testdb");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(mongo)
                    .WaitFor(mongo)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using MongoDB.Driver;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddMongoDBClient("testdb");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (IMongoClient client) =>
                {
                    var db = client.GetDatabase("testdb");
                    var collection = db.GetCollection<MongoDB.Bson.BsonDocument>("test_collection");

                    var testDoc = new MongoDB.Bson.BsonDocument("key", $"test-{Guid.NewGuid():N}");
                    await collection.InsertOneAsync(testDoc);

                    var found = await collection.Find(new MongoDB.Bson.BsonDocument("_id", testDoc["_id"])).FirstOrDefaultAsync();

                    // Cleanup
                    await collection.DeleteOneAsync(new MongoDB.Bson.BsonDocument("_id", testDoc["_id"]));

                    if (found is not null)
                    {
                        return Results.Ok("PASSED: MongoDB insert+find works");
                    }
                    return Results.Problem("FAILED: inserted document not found");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.MongoDB"],
                apiClientPackages: ["Aspire.MongoDB.Driver"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18084,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithMySql()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var mysql = builder.AddMySql("mysql").AddDatabase("testdb");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(mysql)
                    .WaitFor(mysql)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using MySqlConnector;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddMySqlDataSource("testdb");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (MySqlDataSource dataSource) =>
                {
                    await using var conn = await dataSource.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 AS result";
                    var result = await cmd.ExecuteScalarAsync();

                    if (result is int val && val == 1)
                    {
                        return Results.Ok("PASSED: MySQL SELECT 1 works");
                    }
                    return Results.Problem($"FAILED: expected 1, got '{result}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.MySql"],
                apiClientPackages: ["Aspire.MySqlConnector"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18085,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithSqlServer()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var sql = builder.AddSqlServer("sql").AddDatabase("testdb");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(sql)
                    .WaitFor(sql)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using Microsoft.Data.SqlClient;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddSqlServerClient("testdb");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (SqlConnection connection) =>
                {
                    await connection.OpenAsync();
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT 1 AS result";
                    var result = await cmd.ExecuteScalarAsync();

                    if (result is int val && val == 1)
                    {
                        return Results.Ok("PASSED: SQL Server SELECT 1 works");
                    }
                    return Results.Problem($"FAILED: expected 1, got '{result}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.SqlServer"],
                apiClientPackages: ["Aspire.Microsoft.Data.SqlClient"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18086,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithGarnet()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var cache = builder.AddGarnet("cache");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(cache)
                    .WaitFor(cache)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            // Garnet is Redis-protocol compatible, so we use the Redis client
            var apiProgramCode = """
                using StackExchange.Redis;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddRedisClient("cache");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (IConnectionMultiplexer redis) =>
                {
                    var db = redis.GetDatabase();

                    var testKey = $"test-{Guid.NewGuid():N}";
                    await db.StringSetAsync(testKey, "hello-from-garnet");
                    var value = await db.StringGetAsync(testKey);
                    await db.KeyDeleteAsync(testKey);

                    if (value == "hello-from-garnet")
                    {
                        return Results.Ok("PASSED: Garnet SET+GET works");
                    }
                    return Results.Problem($"FAILED: expected 'hello-from-garnet', got '{value}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Garnet"],
                apiClientPackages: ["Aspire.StackExchange.Redis"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18087,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithValkey()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var cache = builder.AddValkey("cache");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(cache)
                    .WaitFor(cache)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            // Valkey is Redis-protocol compatible, so we use the Redis client
            var apiProgramCode = """
                using StackExchange.Redis;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddRedisClient("cache");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (IConnectionMultiplexer redis) =>
                {
                    var db = redis.GetDatabase();

                    var testKey = $"test-{Guid.NewGuid():N}";
                    await db.StringSetAsync(testKey, "hello-from-valkey");
                    var value = await db.StringGetAsync(testKey);
                    await db.KeyDeleteAsync(testKey);

                    if (value == "hello-from-valkey")
                    {
                        return Results.Ok("PASSED: Valkey SET+GET works");
                    }
                    return Results.Problem($"FAILED: expected 'hello-from-valkey', got '{value}'");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Valkey"],
                apiClientPackages: ["Aspire.StackExchange.Redis"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18088,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployWithNats()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var nats = builder.AddNats("nats");

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(nats)
                    .WaitFor(nats)
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                using NATS.Client.Core;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddNatsClient("nats");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", (INatsConnection nats) =>
                {
                    var status = nats.ConnectionState;

                    if (status == NatsConnectionState.Open)
                    {
                        return Results.Ok("PASSED: NATS connection is open");
                    }
                    return Results.Problem($"FAILED: NATS connection state is {status}");
                });

                app.Run();
                """;

            KubernetesDeployTestHelpers.ScaffoldK8sDeployProject(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Nats"],
                apiClientPackages: ["Aspire.NATS.Net"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("chartversion", "0.1.0"),
                    ("namespace", k8sNamespace),
                    ("registryendpoint", "localhost:5001"),
                ]);

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18089,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }

        await pendingRun;
    }
}
