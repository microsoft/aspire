// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes that proves the
/// <c>WithPersistentVolume(volume, mountPath)</c> overload works for project
/// resources — closes the scenario tracked by <c>aspire/issues/9430</c>.
///
/// Scenario: a project mounts a first-class persistent volume at <c>/srv/data</c>,
/// writes a marker file on its first /test-deployment hit, then we delete the
/// project pod (StatefulSet auto-promotion applies because the project is bound
/// to a PV) and verify the marker survives the restart.
/// </summary>
public sealed class KubernetesDeployWithProjectPersistentVolumeTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployProjectPvTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithProjectPersistentVolumeSurvivesPodRestart()
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

            // Mount-path overload of WithPersistentVolume — works for ProjectResource
            // (no ContainerMountAnnotation needs to pre-exist; the overload adds one
            // itself) and triggers StatefulSet auto-promotion just like the name-match
            // overload.
            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE002, ASPIRECOMPUTE003
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                builder.AddContainerRegistry("registry", registryEndpoint);

                var k8s = builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                var scratch = k8s.AddPersistentVolume("scratch")
                    .WithStorageClass("standard")
                    .WithCapacity("256Mi")
                    .WithAccessMode(PersistentVolumeAccessMode.ReadWriteOnce);

                builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithPersistentVolume(scratch, "/srv/data")
                    .WithExternalHttpEndpoints();

                builder.Build().Run();
                """;

            // Two-action endpoint mirroring the postgres test, but writing to a file
            // on the mounted PV instead of a database table.
            var apiProgramCode = """
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();

                var app = builder.Build();
                app.MapDefaultEndpoints();

                const string MarkerPath = "/srv/data/marker.txt";
                const string MarkerToken = "wrote-42";

                app.MapGet("/test-deployment", (string? action) =>
                {
                    if (action == "write")
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                        File.WriteAllText(MarkerPath, MarkerToken);
                        return Results.Ok("PASSED: wrote " + MarkerToken);
                    }

                    if (action == "read")
                    {
                        if (!File.Exists(MarkerPath))
                        {
                            return Results.Problem("FAILED: marker file missing at " + MarkerPath);
                        }
                        var content = File.ReadAllText(MarkerPath);
                        if (content == MarkerToken)
                        {
                            return Results.Ok("PASSED: read " + content);
                        }
                        return Results.Problem("FAILED: expected '" + MarkerToken + "', got '" + content + "'");
                    }

                    return Results.BadRequest("missing or invalid 'action' query parameter (use write|read)");
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

            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("registryendpoint", "localhost:5001"),
                    ("namespace", k8sNamespace),
                    ("chartversion", "0.1.0"),
                ]);

            // === Verify generated shape ===
            // The project is bound to a PV so it must auto-promote to a StatefulSet —
            // there should be no server-deployment, only server-statefulset.
            output.WriteLine("Verify: server StatefulSet exists (project auto-promoted from Deployment)");
            await auto.TypeAsync($"kubectl get sts server-statefulset -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Verify: scratch PVC exists and is Bound");
            await auto.TypeAsync($"kubectl get pvc scratch -n {k8sNamespace} -o jsonpath='{{.status.phase}}' | grep -q Bound && echo PVC_BOUND_OK || {{ echo PVC_NOT_BOUND; exit 1; }}");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PVC_BOUND_OK", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod --all -n {k8sNamespace} --timeout=240s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync($"kubectl get pods -n {k8sNamespace} -o wide");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // === Phase 1: write marker file via the mounted PV ===
            const int LocalPort = 18084;
            await PortForwardServerAsync(auto, counter, k8sNamespace, LocalPort);

            output.WriteLine("Phase 1: write marker file to /srv/data/marker.txt");
            await CurlVerifyAsync(auto, counter, $"http://localhost:{LocalPort}/test-deployment?action=write", "PASSED: wrote wrote-42");

            await KillBackgroundJobAsync(auto, counter);

            // === Phase 2: pod restart ===
            // Delete the project pod. K8s recreates it, the PVC re-attaches, and the
            // marker file should still be there. This proves the mount-path overload
            // wires through the same PVC binding as the name-match overload, and that
            // the project's StatefulSet promotion preserves the volume across restarts.
            output.WriteLine("Phase 2: delete server-statefulset-0 and wait for K8s to recreate it");
            await auto.TypeAsync($"kubectl delete pod server-statefulset-0 -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod server-statefulset-0 -n {k8sNamespace} --timeout=180s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // === Phase 3: read marker — the durability proof ===
            await PortForwardServerAsync(auto, counter, k8sNamespace, LocalPort);

            output.WriteLine("Phase 3: read marker file — proves data survived pod restart");
            await CurlVerifyAsync(auto, counter, $"http://localhost:{LocalPort}/test-deployment?action=read", "PASSED: read wrote-42");

            await KillBackgroundJobAsync(auto, counter);

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }

    private static async Task PortForwardServerAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string @namespace,
        int localPort)
    {
        await auto.TypeAsync($"kubectl port-forward -n {@namespace} svc/server-service {localPort}:8080 > /dev/null 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

        await auto.TypeAsync("sleep 3");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
    }

    private static async Task CurlVerifyAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string url,
        string expectedToken)
    {
        await auto.TypeAsync(
            $"for i in $(seq 1 30); do " +
            $"result=$(curl -s -w '\\nHTTP_%{{http_code}}' '{url}' 2>/dev/null); " +
            $"if echo \"$result\" | grep -q '{expectedToken}'; then echo \"VERIFY_OK: $result\"; break; fi; " +
            $"echo \"Attempt $i: got $result, retrying...\"; sleep 5; done");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(4));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
    }

    private static async Task KillBackgroundJobAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("kill %1 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter);
    }
}
