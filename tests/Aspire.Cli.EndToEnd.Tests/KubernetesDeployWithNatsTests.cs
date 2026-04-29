// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
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
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15335")]
    [CaptureWorkspaceOnFailure]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/15789")]
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

            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
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
