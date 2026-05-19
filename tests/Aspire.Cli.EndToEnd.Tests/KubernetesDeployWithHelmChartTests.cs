// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes with an external Helm chart
/// installed via <c>AddHelmChart</c>. Verifies that both the Aspire application
/// and the external podinfo chart are deployed successfully.
/// </summary>
public sealed class KubernetesDeployWithHelmChartTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sHelmChartTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithExternalHelmChart()
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

        // Prepare environment
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            // =====================================================================
            // Phase 1: Install KinD + Helm, create cluster with local registry
            // =====================================================================

            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // =====================================================================
            // Phase 2: Scaffold the project with AddHelmChart for podinfo
            // =====================================================================

            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithExternalHttpEndpoints();

                var k8s = builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                // Install an external Helm chart (podinfo) alongside the app
                k8s.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
                    .WithHelmValue("replicaCount", "2")
                    .WithHelmValue("ui.message", "Aspire AddHelmChart works!");

                builder.Build().Run();
                """;

            var apiProgramCode = """
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", () =>
                {
                    return Results.Ok("PASSED: API service with external Helm chart");
                });

                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes"],
                apiClientPackages: [],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);

            // =====================================================================
            // Phase 3: Run aspire deploy interactively
            // =====================================================================

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("registryendpoint", "localhost:5001"),
                    ("namespace", k8sNamespace),
                    ("chartversion", "0.1.0"),
                ]);

            // =====================================================================
            // Phase 4: Verify the Aspire app deployment
            // =====================================================================

            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: "server",
                localPort: 18080,
                testPath: "/test-deployment");

            // =====================================================================
            // Phase 5: Verify the external Helm chart (podinfo) was installed
            // =====================================================================

            // Check that podinfo pods are running in its own namespace
            await auto.TypeAsync("kubectl get pods -n podinfo -l app.kubernetes.io/name=podinfo");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Verify podinfo has 2 replicas as configured
            await auto.TypeAsync(
                "READY=$(kubectl get deploy -n podinfo -l app.kubernetes.io/name=podinfo " +
                "-o jsonpath='{.items[0].status.readyReplicas}') && " +
                "[ \"$READY\" = \"2\" ] && echo 'VERIFY_OK: podinfo has 2 replicas' || " +
                "echo \"FAIL: expected 2 replicas, got $READY\"");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Verify the Helm release exists
            await auto.TypeAsync("helm list -n podinfo");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // =====================================================================
            // Phase 6: Cleanup
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
}
