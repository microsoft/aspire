// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes: DeployK8sWithNats.
/// </summary>
public sealed class KubernetesDeployWithNatsTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithNats()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var nats = builder.AddNats("nats");

                var api = builder.AddDotnetProject("server", "../{{ProjectName}}.ApiService/{{ProjectName}}.ApiService.csproj")
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

                // NatsConnection connects lazily, so exercise a publish+subscribe round-trip to
                // establish the connection and verify the deployed credentials are accepted.
                app.MapGet("/test-deployment", async (INatsConnection nats) =>
                {
                    var subject = $"test.{Guid.NewGuid():N}";
                    await using var subscription = await nats.SubscribeCoreAsync<string>(subject);
                    await nats.PublishAsync(subject, "hello-from-k8s");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var message = await subscription.Msgs.ReadAsync(cts.Token);

                    if (message.Data == "hello-from-k8s")
                    {
                        return Results.Ok("PASSED: NATS publish+subscribe works");
                    }
                    return Results.Problem($"FAILED: unexpected NATS message '{message.Data}'");
                });

                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Nats"],
                apiClientPackages: ["Aspire.NATS.Net"],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);

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
                localPort: 18089,
                testPath: "/test-deployment");

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
