// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes that proves data written to a
/// first-class <c>KubernetesPersistentVolumeResource</c> survives a pod restart.
///
/// Scenario: Postgres bound by name match to a persistent volume — exercises
/// <c>WithDataVolume()</c> + <c>WithPersistentVolume(volume)</c>, the auto-promotion
/// of the workload from <c>Deployment</c> to <c>StatefulSet</c>, and the generated
/// PVC binding through to the rancher local-path-provisioner that ships with KinD.
/// </summary>
public sealed class KubernetesDeployWithPersistentVolumeTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployPvTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithPostgresPersistentVolumeSurvivesPodRestart()
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

            // First-class PV bound to Postgres via name match. Postgres' WithDataVolume()
            // emits a ContainerMountAnnotation source = "pg-data" — the binding rewrites
            // the pod's volumes[] entry to reference the generated PVC and auto-promotes
            // the workload to a StatefulSet. KinD's default StorageClass "standard" is
            // backed by the rancher local-path-provisioner (RWO host-path), which keeps
            // PVC contents across pod restarts within the cluster lifetime.
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

                var pgData = k8s.AddPersistentVolume("pg-data")
                    .WithStorageClass("standard")
                    .WithCapacity("1Gi")
                    .WithAccessMode(PersistentVolumeAccessMode.ReadWriteOnce);

                // Pass an explicit volume name to WithDataVolume so the auto-generated
                // "{AppHost}.{hash}-pg-data" form is not used. The auto-generated name
                // contains a dot ('.'), which the Kubernetes publisher currently
                // propagates verbatim into the StatefulSet podSpec volumes[].name slot;
                // K8s rejects dots there ("DNS_LABEL"). Tracked as a follow-up bug
                // against the WithPersistentVolume name-match binding.
                var postgres = builder.AddPostgres("pg")
                    .WithDataVolume("pg-data")
                    .WithPersistentVolume(pgData);

                builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithReference(postgres)
                    .WaitFor(postgres)
                    .WithExternalHttpEndpoints();

                builder.Build().Run();
                """;

            // Two-action endpoint: ?action=write seeds the durability table; ?action=read
            // verifies the row is still there. This shape lets the test prove durability
            // by interleaving curl calls with a kubectl pod-delete in between.
            var apiProgramCode = """
                using Npgsql;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                builder.AddNpgsqlDataSource("pg");

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", async (string? action, NpgsqlDataSource dataSource) =>
                {
                    await using var conn = await dataSource.OpenConnectionAsync();

                    if (action == "write")
                    {
                        await using (var create = conn.CreateCommand())
                        {
                            create.CommandText = "CREATE TABLE IF NOT EXISTS durability(id int PRIMARY KEY)";
                            await create.ExecuteNonQueryAsync();
                        }
                        await using (var insert = conn.CreateCommand())
                        {
                            insert.CommandText = "INSERT INTO durability VALUES (42) ON CONFLICT DO NOTHING";
                            await insert.ExecuteNonQueryAsync();
                        }
                        return Results.Ok("PASSED: wrote 42");
                    }

                    if (action == "read")
                    {
                        await using var select = conn.CreateCommand();
                        select.CommandText = "SELECT id FROM durability WHERE id = 42";
                        var result = await select.ExecuteScalarAsync();
                        if (result is int id && id == 42)
                        {
                            return Results.Ok("PASSED: read 42");
                        }
                        return Results.Problem($"FAILED: expected 42, got '{result ?? "null"}'");
                    }

                    return Results.BadRequest("missing or invalid 'action' query parameter (use write|read)");
                });

                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.PostgreSQL"],
                apiClientPackages: ["Aspire.Npgsql"],
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
            // PV-bound workloads are auto-promoted to StatefulSet. The generated names
            // come from HelmExtensions.ToStatefulSetName / ToKubernetesResourceName so a
            // change to those would break this assertion — that's intentional, the
            // durability story relies on stable naming across redeploys.
            output.WriteLine("Verify: pg StatefulSet exists (auto-promoted from Deployment)");
            await auto.TypeAsync($"kubectl get sts pg-statefulset -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Verify: pg-data PVC exists and is Bound");
            await auto.TypeAsync($"kubectl get pvc pg-data -n {k8sNamespace} -o jsonpath='{{.status.phase}}' | grep -q Bound && echo PVC_BOUND_OK || {{ echo PVC_NOT_BOUND; exit 1; }}");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PVC_BOUND_OK", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Wait for both pods (server Deployment + pg StatefulSet) to be Ready.
            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod --all -n {k8sNamespace} --timeout=240s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync($"kubectl get pods -n {k8sNamespace} -o wide");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // === Phase 1: write data ===
            const int LocalPort = 18083;
            await PortForwardServerAsync(auto, counter, k8sNamespace, LocalPort);

            output.WriteLine("Phase 1: write durability row through server -> pg");
            await CurlVerifyAsync(auto, counter, $"http://localhost:{LocalPort}/test-deployment?action=write", "PASSED: wrote 42");

            // Stop the port-forward before deleting the postgres pod — the next
            // port-forward will target the freshly recreated pod.
            await KillBackgroundJobAsync(auto, counter);

            // === Phase 2: pod restart ===
            // Delete the postgres pod. The StatefulSet controller recreates it, K8s
            // re-attaches the same PVC, and the server pod's connection pool will
            // reconnect on the next request. If the abstraction is wrong (e.g. the
            // workload had rendered as a Deployment with an emptyDir, or the publisher
            // generates a fresh PVC name on each render), the row will be gone.
            output.WriteLine("Phase 2: delete pg-statefulset-0 and wait for K8s to recreate it");
            await auto.TypeAsync($"kubectl delete pod pg-statefulset-0 -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod pg-statefulset-0 -n {k8sNamespace} --timeout=180s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // === Phase 3: read data — the durability proof ===
            await PortForwardServerAsync(auto, counter, k8sNamespace, LocalPort);

            output.WriteLine("Phase 3: read durability row — proves data survived pod restart");
            await CurlVerifyAsync(auto, counter, $"http://localhost:{LocalPort}/test-deployment?action=read", "PASSED: read 42");

            await KillBackgroundJobAsync(auto, counter);

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
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
        // Redirect port-forward output to /dev/null to keep prompt detection clean —
        // "Forwarding from..." and "Handling connection for..." chatter otherwise
        // collides with the SequenceCounter-based prompt scanner.
        await auto.TypeAsync($"kubectl port-forward -n {@namespace} svc/server-service {localPort}:8080 > /dev/null 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

        // Brief pause so the port-forward has time to bind before the first curl.
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
        // Retry up to 30 times (~150s) — Postgres needs a few seconds after pod
        // restart for the listener to come back, and the server's connection pool
        // takes a beat to retry.
        //
        // Use a sentinel that the shell only produces at runtime via command
        // substitution: the typed text contains "DUR$(echo AB)_OK_PASS" while
        // the executed echo emits "DURAB_OK_PASS". This prevents WaitUntilTextAsync
        // from matching the typed echo of the for-loop itself (which would race
        // ahead of the curl loop actually completing).
        await auto.TypeAsync(
            $"for i in $(seq 1 30); do " +
            $"result=$(curl -s -w '\\nHTTP_%{{http_code}}' '{url}' 2>/dev/null); " +
            $"if echo \"$result\" | grep -q '{expectedToken}'; then echo \"DUR$(echo AB)_OK_PASS: $result\"; break; fi; " +
            $"echo \"Attempt $i: got $result, retrying...\"; sleep 5; done");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("DURAB_OK_PASS", timeout: TimeSpan.FromMinutes(4));
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
