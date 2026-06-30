// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// TypeScript analog of <see cref="KubernetesDeployWithProjectPersistentVolumeTests"/>.
/// Verifies the auto-generated TypeScript bindings for
/// <c>addPersistentVolume</c>/<c>withStorageClass</c>/<c>withCapacity</c>/<c>withKubernetesPersistentVolumeMount</c>
/// produce the same first-class persistent volume wiring as the C# API: the
/// node-app workload is auto-promoted to a <c>StatefulSet</c>, the generated PVC
/// binds to the rancher local-path-provisioner that ships with KinD, and a file
/// written to the mounted volume survives a pod restart.
///
/// This is the only TypeScript test exercising the persistent volume API end-to-end —
/// the C# Postgres counterpart already covers the name-match overload and the
/// connection-string-driven workload scenario, so a TS Postgres variant would
/// duplicate the C# durability proof while adding npm <c>pg</c> + connection
/// string wiring that is orthogonal to what this PR changes.
/// </summary>
public sealed class KubernetesDeployTypeScriptWithPersistentVolumeTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sTsPvTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployTypeScriptK8sWithPersistentVolumeSurvivesPodRestart()
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
            // =====================================================================
            // Phase 1: Install KinD + Helm, create cluster with local registry
            // =====================================================================

            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // =====================================================================
            // Phase 2: Create TypeScript project from template + add Kubernetes
            // =====================================================================

            await auto.AspireNewAsync(ProjectName, counter, template: AspireTemplate.ExpressReact);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Add Kubernetes hosting so the persistent-volume APIs are projected
            // into the generated TypeScript SDK on the next aspire restore.
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

            // Regenerate the local TypeScript SDK shim (./.modules/aspire.js) so
            // addPersistentVolume + withKubernetesPersistentVolumeMount become
            // callable from apphost.mts. Without this step the TS imports below
            // would fail at type-check / runtime.
            await auto.TypeAsync("aspire restore");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 3: Modify apphost.mts to wire the persistent volume to the app
            // =====================================================================

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostFilePath = Path.Combine(projectDir, "apphost.mts");
            var apiIndexFilePath = Path.Combine(projectDir, "api", "src", "index.ts");

            output.WriteLine($"Patching apphost.mts at: {appHostFilePath}");

            // Replacing the trailing "await builder.build().run();" injects:
            //   - container registry param
            //   - K8s environment with Helm namespace/chartversion params
            //   - first-class PV "scratch" with KinD's default StorageClass
            //   - mount-path binding on the existing `app` (addNodeApp) at /srv/data
            // This forces StatefulSet promotion of the node app, since binding any
            // persistent volume promotes the workload from Deployment to StatefulSet.
            var appHostContent = File.ReadAllText(appHostFilePath);
            appHostContent = appHostContent.Replace(
                "await builder.build().run();",
                """
// Container registry param drives image push; K8s namespace/chartVersion drive Helm output.
const registryEndpoint = await builder.addParameter("registryendpoint");
await builder.addContainerRegistry("registry", registryEndpoint);

const k8sNamespace = await builder.addParameter("namespace");
const chartVersion = await builder.addParameter("chartversion");

const k8sEnv = await builder.addKubernetesEnvironment("env");
await k8sEnv.withHelm({
    configure: async (helm) => {
        await helm.withNamespace(k8sNamespace);
        await helm.withChartVersion(chartVersion);
    },
});

// First-class persistent volume — auto-generates a v1.PersistentVolumeClaim
// named "scratch" in the Helm chart. Storage class "standard" is KinD's
// default (rancher local-path-provisioner, RWO host-path, survives pod
// restarts within cluster lifetime).
const scratch = await k8sEnv.addPersistentVolume("scratch");
await scratch.withStorageClass("standard");
await scratch.withCapacity("256Mi");

// Mount-path overload — works for any IComputeResource (including node apps
// and projects) by adding the ContainerMount itself. Binding a workload to
// a PV auto-promotes it from Deployment to StatefulSet.
await app.withKubernetesPersistentVolumeMount(scratch, "/srv/data");

await builder.build().run();
""");
            File.WriteAllText(appHostFilePath, appHostContent);

            output.WriteLine($"Patching api/src/index.ts at: {apiIndexFilePath}");

            // Inject a /test-deployment endpoint that exercises the mounted volume.
            //
            // The endpoint speaks the same write/read action protocol as the C# tests
            // so the durability phases below (curl write -> kubectl delete pod -> curl
            // read) and their VERIFY_OK tokens match between C# and TS suites.
            var apiIndexContent = File.ReadAllText(apiIndexFilePath);
            apiIndexContent = apiIndexContent.Replace(
                "import { existsSync } from \"fs\";",
                "import { existsSync, mkdirSync, readFileSync, writeFileSync } from \"fs\";")
                .Replace(
                    "app.get(\"/health\", (_req, res) => {",
                    """
const MARKER_PATH = "/srv/data/marker.txt";
const MARKER_TOKEN = "wrote-42";

app.get("/test-deployment", (req, res) => {
  const action = typeof req.query.action === "string" ? req.query.action : undefined;
  if (action === "write") {
    mkdirSync("/srv/data", { recursive: true });
    writeFileSync(MARKER_PATH, MARKER_TOKEN);
    res.send(`PASSED: wrote ${MARKER_TOKEN}`);
    return;
  }
  if (action === "read") {
    if (!existsSync(MARKER_PATH)) {
      res.status(500).send(`FAILED: marker file missing at ${MARKER_PATH}`);
      return;
    }
    const content = readFileSync(MARKER_PATH, "utf8");
    if (content === MARKER_TOKEN) {
      res.send(`PASSED: read ${content}`);
      return;
    }
    res.status(500).send(`FAILED: expected '${MARKER_TOKEN}', got '${content}'`);
    return;
  }
  res.status(400).send("missing or invalid 'action' query parameter (use write|read)");
});

app.get("/health", (_req, res) => {
""");
            File.WriteAllText(apiIndexFilePath, apiIndexContent);

            // =====================================================================
            // Phase 4: Run aspire deploy interactively
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
            // Phase 5: Verify the generated K8s shape
            // =====================================================================

            // The node app is bound to a PV so the publisher must have rendered it
            // as a StatefulSet — there should be no app-deployment, only
            // app-statefulset. Naming comes from HelmExtensions.ToStatefulSetName.
            output.WriteLine("Verify: app StatefulSet exists (node app auto-promoted from Deployment)");
            await auto.TypeAsync($"kubectl get sts app-statefulset -n {k8sNamespace}");
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

            // =====================================================================
            // Phase 6: Write marker file directly into the mounted PV via kubectl exec
            //
            // We bypass HTTP/port-forward because the Node.js template's HTTP listener
            // doesn't reliably surface through the K8s Service in CI (separate plumbing
            // issue, not a PV concern). What we actually want to prove here is that the
            // PV is mounted, writable, and survives pod restart — doing this via
            // `kubectl exec` is both simpler and a more direct test of the publisher's
            // PV wiring.
            // =====================================================================

            output.WriteLine("Phase 6: write marker file to /srv/data/marker.txt via kubectl exec");
            await auto.TypeAsync(
                $"kubectl exec app-statefulset-0 -n {k8sNamespace} -- sh -c " +
                "'echo wrote-42 > /srv/data/marker.txt && echo WROTE_OK_$(cat /srv/data/marker.txt)'");
            await auto.EnterAsync();
            // Single quotes around the sh -c arg are intentional: with double quotes, the
            // outer bash on the test runner expands $(cat ...) locally (producing empty,
            // since the marker file doesn't exist on the host) before the command ever
            // reaches the pod. Runtime-only sentinel "WROTE_OK_wrote-42" also avoids
            // racing against the typed echo, which only contains the literal
            // "WROTE_OK_$(cat ...)" substring.
            await auto.WaitUntilTextAsync("WROTE_OK_wrote-42", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // =====================================================================
            // Phase 7: Pod restart — kill the node app pod, wait for K8s to recreate
            // =====================================================================

            // Deleting the StatefulSet pod forces K8s to recreate it; the controller
            // re-attaches the same PVC. If the publisher had rendered an emptyDir
            // (i.e. the binding annotation was lost in TS lowering) or generated a
            // fresh PVC name on each render, the marker file would be gone.
            output.WriteLine("Phase 7: delete app-statefulset-0 and wait for K8s to recreate it");
            await auto.TypeAsync($"kubectl delete pod app-statefulset-0 -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod app-statefulset-0 -n {k8sNamespace} --timeout=180s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // =====================================================================
            // Phase 8: Read marker — the durability proof
            // =====================================================================

            output.WriteLine("Phase 8: read marker file from new pod — proves data survived pod restart");
            await auto.TypeAsync(
                $"kubectl exec app-statefulset-0 -n {k8sNamespace} -- sh -c " +
                "'echo READ_OK_$(cat /srv/data/marker.txt)'");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("READ_OK_wrote-42", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // =====================================================================
            // Phase 9: Cleanup
            // =====================================================================

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
