// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes: DeployK8sWithRedis.
/// </summary>
public sealed class KubernetesDeployWithRedisTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployTest";

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15335")]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithRedis()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // Scaffold: AppHost with Redis + API service that uses Redis client
            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
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

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Redis"],
                apiClientPackages: ["Aspire.StackExchange.Redis"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);

            // Deploy prompts: registryendpoint, namespace, chartversion (code declaration order)
            // Redis also generates a password parameter (cache_password)
            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("registryendpoint", "localhost:5001"),
                    ("namespace", k8sNamespace),
                    ("chartversion", "0.1.0"),
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
}
