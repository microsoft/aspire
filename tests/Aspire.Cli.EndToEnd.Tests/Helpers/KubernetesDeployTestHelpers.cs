// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Shared helpers for Kubernetes deploy E2E tests that use KinD clusters with a local registry.
/// </summary>
internal static class KubernetesDeployTestHelpers
{
    private const string KindRegistryOwnerLabel = "aspire.e2e.kind-registry-owner";

    private static string KindVersion => KubernetesE2EVersions.KindVersion;
    private static string HelmVersion => KubernetesE2EVersions.HelmVersion;
    private static string KubectlVersion => KubernetesE2EVersions.KubectlVersion;

    /// <summary>
    /// Generates a unique KinD cluster name (max 32 chars).
    /// </summary>
    internal static string GenerateUniqueClusterName() =>
        $"aspire-e2e-{Guid.NewGuid():N}"[..32];

    /// <summary>
    /// Installs KinD, Helm, and kubectl binaries to ~/.local/bin and adds to PATH.
    /// Skips downloads for tools already on PATH (e.g., pre-installed in Dockerfile.e2e).
    /// Retries downloads up to 3 times to handle transient GitHub CDN failures.
    /// </summary>
    internal static async Task InstallKindAndHelmAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.TypeAsync("mkdir -p ~/.local/bin");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Download KinD if not already installed — GitHub CDN can transiently return HTML instead of a binary.
        await auto.TypeAsync($"command -v kind >/dev/null 2>&1 || {{ rm -f ~/.local/bin/kind; for i in 1 2 3; do curl -sSLo ~/.local/bin/kind \"https://github.com/kubernetes-sigs/kind/releases/download/{KindVersion}/kind-linux-amd64\" && chmod +x ~/.local/bin/kind && ~/.local/bin/kind version >/dev/null 2>&1 && break; echo \"Retry $i: KinD download failed, retrying in 5s...\"; rm -f ~/.local/bin/kind; sleep 5; done; test -x ~/.local/bin/kind && ~/.local/bin/kind version >/dev/null 2>&1; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        // Download Helm if not already installed
        await auto.TypeAsync($"command -v helm >/dev/null 2>&1 || {{ for i in 1 2 3; do curl -sSL https://get.helm.sh/helm-{HelmVersion}-linux-amd64.tar.gz | tar xz -C /tmp && test -f /tmp/linux-amd64/helm && break; echo \"Retry $i: Helm download failed, retrying in 5s...\"; sleep 5; done && mv /tmp/linux-amd64/helm ~/.local/bin/helm && rm -rf /tmp/linux-amd64; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        // Download kubectl if not already installed
        await auto.TypeAsync($"command -v kubectl >/dev/null 2>&1 || {{ rm -f ~/.local/bin/kubectl; for i in 1 2 3; do curl -sSLo ~/.local/bin/kubectl \"https://dl.k8s.io/release/{KubectlVersion}/bin/linux/amd64/kubectl\" && chmod +x ~/.local/bin/kubectl && ~/.local/bin/kubectl version --client >/dev/null 2>&1 && break; echo \"Retry $i: kubectl download failed, retrying in 5s...\"; rm -f ~/.local/bin/kubectl; sleep 5; done; test -x ~/.local/bin/kubectl && ~/.local/bin/kubectl version --client >/dev/null 2>&1; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        await auto.TypeAsync("export PATH=\"$HOME/.local/bin:$PATH\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify all three binaries are functional
        await auto.TypeAsync("kind version && helm version --short && kubectl version --client --short 2>/dev/null || kubectl version --client");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Creates a KinD cluster with a local Docker registry at localhost:5001.
    /// Follows the KinD local registry guide pattern.
    /// </summary>
    internal static async Task CreateKindClusterWithRegistryAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string clusterName)
    {
        // Delete any leftover cluster with the same name
        await auto.TypeAsync($"kind delete cluster --name={clusterName} 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Label only registries this test creates. A pre-existing registry can belong to a
        // developer or another cluster, so neither cleanup path may remove it. Do not start
        // a stopped pre-existing registry on its owner's behalf; fail setup explicitly.
        await auto.TypeAsync(
            "if docker container inspect kind-registry >/dev/null 2>&1; then " +
            "test \"$(docker inspect -f '{{.State.Running}}' kind-registry)\" = true || " +
            "{ echo 'Existing kind-registry is stopped; refusing to modify it.' >&2; false; }; " +
            "else docker run -d --restart=always -p 5001:5000 --network bridge --name kind-registry " +
            $"--label '{KindRegistryOwnerLabel}={clusterName}' registry:2; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Create the cluster (no containerd config patches — registry is configured post-creation via hosts.toml)
        await auto.TypeAsync($"kind create cluster --name={clusterName} --wait=120s");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        // Connect registry to cluster network
        await auto.TypeAsync($"docker network connect \"kind\" kind-registry 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The cluster is created by the host Docker daemon, so the default kubeconfig points kubectl at a
        // localhost-published API server port that is not reachable from inside the helper container. Join the
        // helper container to the kind network and switch kubectl to the cluster's internal control-plane endpoint.
        await auto.TypeAsync($"docker network connect \"kind\" \"$(hostname)\" 2>/dev/null || true && kind export kubeconfig --name={clusterName} --internal >/dev/null");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Configure containerd on each node to resolve localhost:5001 via the registry container.
        // This uses the config_path approach required by containerd v2+ (shipped in KinD v0.31.0+).
        await auto.TypeAsync($"for node in $(kind get nodes --name={clusterName}); do " +
            "docker exec \"$node\" mkdir -p /etc/containerd/certs.d/localhost:5001 && " +
            "echo '[host.\"http://kind-registry:5000\"]' | docker exec -i \"$node\" tee /etc/containerd/certs.d/localhost:5001/hosts.toml > /dev/null && " +
            "echo '  capabilities = [\"pull\", \"resolve\"]' | docker exec -i \"$node\" tee -a /etc/containerd/certs.d/localhost:5001/hosts.toml > /dev/null; " +
            "done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Create a ConfigMap so KinD knows about the local registry
        await auto.TypeAsync("cat > /tmp/local-registry-cm.yaml << 'CMEOF'");
        await auto.EnterAsync();
        await auto.TypeAsync("apiVersion: v1");
        await auto.EnterAsync();
        await auto.TypeAsync("kind: ConfigMap");
        await auto.EnterAsync();
        await auto.TypeAsync("metadata:");
        await auto.EnterAsync();
        await auto.TypeAsync("  name: local-registry-hosting");
        await auto.EnterAsync();
        await auto.TypeAsync("  namespace: kube-public");
        await auto.EnterAsync();
        await auto.TypeAsync("data:");
        await auto.EnterAsync();
        await auto.TypeAsync("  localRegistryHosting.v1: |");
        await auto.EnterAsync();
        await auto.TypeAsync("    host: \"localhost:5001\"");
        await auto.EnterAsync();
        await auto.TypeAsync("CMEOF");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("kubectl apply -f /tmp/local-registry-cm.yaml");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify cluster is ready
        await auto.TypeAsync($"kubectl cluster-info --context kind-{clusterName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Radius <c>rad</c> CLI into ~/.local/bin (already added to PATH
    /// by <see cref="InstallKindAndHelmAsync"/>) using the official install script,
    /// pinned to <see cref="KubernetesE2EVersions.RadiusVersion"/>. Skips the
    /// download when <c>rad</c> is already on PATH, and retries up to 3 times to
    /// tolerate transient GitHub CDN failures (mirrors the KinD/Helm downloads).
    /// </summary>
    internal static async Task InstallRadCliAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        var radiusVersion = KubernetesE2EVersions.RadiusVersion;

        await auto.TypeAsync("mkdir -p ~/.local/bin");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The install script is fetched from the matching release tag (which carries
        // the leading `v`), while its `--version` flag expects the bare number. rad
        // downloads its companion `bicep` to ~/.rad/bin on the first `rad deploy`.
        await auto.TypeAsync($"command -v rad >/dev/null 2>&1 || {{ rm -f ~/.local/bin/rad; for i in 1 2 3; do curl -fsSL \"https://raw.githubusercontent.com/radius-project/radius/v{radiusVersion}/deploy/install.sh\" | /bin/bash -s -- --version {radiusVersion} --install-dir \"$HOME/.local/bin\" && test -x \"$HOME/.local/bin/rad\" && break; echo \"Retry $i: rad download failed, retrying in 5s...\"; rm -f ~/.local/bin/rad; sleep 5; done; test -x \"$HOME/.local/bin/rad\"; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("export PATH=\"$HOME/.local/bin:$PATH\" && rad version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Installs the Radius control plane onto the KinD cluster and creates the
    /// resource group, environment, and workspace that <c>rad deploy</c> (driven by
    /// <c>aspire deploy</c>) resolves against. No Azure is involved.
    /// </summary>
    /// <remarks>
    /// Two non-obvious behaviors this sequence works around, both confirmed against
    /// rad 0.59.0:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>rad</c> ignores <c>KUBECONFIG</c> and targets the current-context of
    ///     <c>~/.kube/config</c>. In this container <c>kind export kubeconfig
    ///     --internal</c> has already set that to <c>kind-&lt;cluster&gt;</c>, but we
    ///     still pass <c>--kubecontext</c>/<c>--context</c> explicitly so the
    ///     control-plane install and workspace are pinned to the intended cluster.
    ///   </description></item>
    ///   <item><description>
    ///     <c>rad install kubernetes</c> provisions the <c>default</c> resource group
    ///     and environment, but does NOT persist a workspace, so <c>rad deploy</c>
    ///     would otherwise fail to resolve a workspace scope. We create the workspace
    ///     explicitly; the <c>rad group create</c>/<c>rad env create</c> calls below are
    ///     defensive (idempotent) so the sequence still succeeds even if a future
    ///     <c>rad</c> stops creating the group/environment during install.
    ///   </description></item>
    /// </list>
    /// </remarks>
    internal static async Task InstallRadiusControlPlaneAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string clusterName)
    {
        // Installing the control plane pulls several images and waits for the
        // radius-system pods to become ready, so allow a generous budget.
        await auto.TypeAsync($"rad install kubernetes --kubecontext kind-{clusterName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

        await auto.TypeAsync("rad group create default");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("rad env create default --group default");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync($"rad workspace create kubernetes radius-e2e --context kind-{clusterName} --group default --environment default --force");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Scaffolds an Aspire project using <c>aspire new</c> (Starter template, no Redis),
    /// then adds hosting/client packages and injects custom code into the existing source files.
    /// Asserts the "Using project templates version:" message appears with a prerelease suffix.
    /// </summary>
    internal static async Task ScaffoldK8sDeployProjectAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string projectName,
        string projectDir,
        string[] appHostHostingPackages,
        string[] apiClientPackages,
        string appHostCode,
        string apiProgramCode,
        ITestOutputHelper output)
    {
        // Step 1: Run aspire new inline (rather than AspireNewAsync) so we can assert on
        // the "Using project templates version:" message that appears during execution.
        await auto.TypeAsync("aspire new");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> Starter App").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(60),
            description: "template selection list (> Starter App)");
        await auto.EnterAsync(); // Select Starter template

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the project name").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "project name prompt");
        await auto.TypeAsync(projectName);
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the output path").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "output path prompt");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Use *.dev.localhost URLs").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "URLs prompt");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Use Redis Cache").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "Redis cache prompt");
        await auto.TypeAsync("n");

        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Do you want to create a test project?").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "test project prompt");
        await auto.EnterAsync();

        // === KEY ASSERTION: Wait for "Using project templates version:" ===
        // This message appears after all prompts, during template installation/project creation.
        var templateVersionSearcher = new CellPatternSearcher().Find("Using project templates version:");
        var agentInitSearcher = new CellPatternSearcher().Find("configure AI agent environments");
        var templateVersionFound = false;

        await auto.WaitUntilAsync(
            snapshot =>
            {
                if (templateVersionSearcher.Search(snapshot).Count > 0)
                {
                    templateVersionFound = true;
                }

                // Wait until the command finishes (agent init prompt or success prompt)
                if (agentInitSearcher.Search(snapshot).Count > 0)
                {
                    return true;
                }
                var successPrompt = new CellPatternSearcher()
                    .FindPattern(counter.Value.ToString())
                    .RightText(" OK] $ ");
                return successPrompt.Search(snapshot).Count > 0;
            },
            timeout: TimeSpan.FromMinutes(5),
            description: "template version message and aspire new completion");

        Assert.True(templateVersionFound,
            "Expected 'Using project templates version:' message during aspire new, but it was not found. " +
            "This may indicate the CLI is not using the expected development templates.");
        output.WriteLine("✅ Template version message found during aspire new");

        // Dismiss agent init prompt (same as DeclineAgentInitPromptAsync)
        await auto.WaitAsync(500);
        await auto.TypeAsync("n");
        await auto.WaitForAnyPromptAsync(counter);

        // Step 2: cd into the project
        await auto.TypeAsync($"cd {projectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Add hosting packages via aspire add (handles version selection)
        foreach (var package in appHostHostingPackages)
        {
            await auto.TypeAsync($"aspire add {package}");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));
        }

        // Step 4: Add client NuGet packages to ApiService (uses local hive version when available, otherwise falls back to --prerelease)
        foreach (var package in apiClientPackages)
        {
            await auto.TypeAsync(AspireCliShellCommandHelpers.GetDotnetAddPackageCommand($"{projectName}.ApiService", package));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
        }

        // Step 5: Inject custom AppHost.cs and ApiService/Program.cs into the template-created project
        var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
        var apiDir = Path.Combine(projectDir, $"{projectName}.ApiService");

        Assert.True(File.Exists(Path.Combine(appHostDir, "AppHost.cs")));
        Assert.True(File.Exists(Path.Combine(apiDir, $"{projectName}.ApiService.csproj")));
        Assert.True(File.Exists(Path.Combine(apiDir, "Properties", "launchSettings.json")));
        output.WriteLine($"Writing AppHost.cs to: {Path.Combine(appHostDir, "AppHost.cs")}");
        File.WriteAllText(Path.Combine(appHostDir, "AppHost.cs"), appHostCode);
        File.WriteAllText(Path.Combine(apiDir, "Program.cs"), apiProgramCode);
    }

    /// <summary>
    /// Runs <c>aspire deploy</c> interactively, answering parameter prompts via terminal automation.
    /// </summary>
    /// <param name="auto">The terminal automator.</param>
    /// <param name="counter">Sequence counter for prompt tracking.</param>
    /// <param name="parameterResponses">
    /// Ordered list of (promptSubstring, valueToType) tuples.
    /// Each entry matches by the parameter name appearing in the prompt text.
    /// Entries are consumed in order — first match wins.
    /// </param>
    /// <param name="outputDir">Optional output directory for publish artifacts.</param>
    internal static async Task AspireDeployInteractiveAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        IReadOnlyList<(string PromptText, string Value)> parameterResponses,
        string? outputDir = null)
    {
        var outputArg = outputDir is not null ? $" -o {outputDir}" : "";
        await auto.TypeAsync($"aspire deploy{outputArg}");
        await auto.EnterAsync();

        // Answer each parameter prompt in order.
        // The CLI shows parameter prompts via Spectre.Console TextPrompt with the parameter name as the label.
        // For multi-input forms, each input appears on its own line as "paramname: ".
        for (var i = 0; i < parameterResponses.Count; i++)
        {
            var (promptText, value) = parameterResponses[i];

            await auto.WaitUntilTextAsync(promptText, timeout: TimeSpan.FromMinutes(5));
            await auto.TypeAsync(value);
            await auto.EnterAsync();
        }

        // Wait for pipeline completion
        await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(10));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Verifies a K8s deployment by port-forwarding and curling the test endpoint.
    /// An optional exact response can be supplied instead of the default PASSED marker.
    /// </summary>
    internal static async Task VerifyDeploymentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string @namespace,
        string serviceName,
        int localPort,
        string testPath = "/test-deployment",
        string? expectedResponse = null)
    {
        // Wait for all pods to be ready in the namespace
        await auto.TypeAsync($"kubectl wait --for=condition=Ready pod --all -n {@namespace} --timeout=180s");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

        // Show pod status for debugging
        await auto.TypeAsync($"kubectl get pods -n {@namespace}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Show server pod logs for debugging connectivity issues
        await auto.TypeAsync($"kubectl logs -n {@namespace} -l app={serviceName} --tail=50 2>&1 || true");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Show environment variables in server pod for connection string debugging
        await auto.TypeAsync($"kubectl exec -n {@namespace} deploy/{serviceName}-deployment -- env 2>&1 | grep -iE '(ConnectionStrings|services)' | head -10 || true");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));

        await auto.VerifyForwardedEndpointAsync(
            counter, @namespace, $"svc/{serviceName}-service", localPort, 8080, testPath, expectedResponse);
    }

    /// <summary>
    /// Verifies a specific ready workload uses the image built for this deployment.
    /// </summary>
    internal static async Task VerifyPodImageAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string @namespace,
        string selector,
        string expectedImage)
    {
        await auto.RunCommandAsync(
            $"kubectl wait --for=condition=Ready pod -n {@namespace} -l {selector} --timeout=180s",
            counter, TimeSpan.FromMinutes(4));

        // jsonpath emits one image per line, e.g. localhost:5001/aspire-e2e-abc/server:project-v2.
        // Require exactly one pod/container, rather than accepting an unrelated ready workload.
        await auto.RunCommandAsync(
            $"test \"$(kubectl get pods -n {@namespace} -l {selector} " +
            "-o jsonpath='{range .items[*]}{range .spec.containers[*]}{.image}{\"\\n\"}{end}{end}')\" " +
            $"= '{expectedImage}'",
            counter);
    }

    /// <summary>
    /// Probes a forwarded service or deployment with bounded HTTP retries and scoped cleanup.
    /// </summary>
    internal static async Task VerifyForwardedEndpointAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string @namespace,
        string target,
        int localPort,
        int targetPort,
        string testPath,
        string? expectedResponse)
    {
        var responseCheck = expectedResponse is null
            ? "printf '%s' \"$result\" | grep -q 'PASSED'"
            : $"[ \"$result\" = '{expectedResponse.Replace("'", "'\"'\"'")}' ]";

        // Run in a subshell so the EXIT trap owns only this forward, even on HTTP failure.
        // A prompt-success assertion checks the final `test`, not an echoed success sentinel.
        // Each curl is bounded; retries also absorb startup of the forward without a fixed sleep.
        await auto.RunCommandAsync(
            $"(kubectl port-forward -n {@namespace} {target} {localPort}:{targetPort} > port-forward.log 2>&1 & " +
            "forward_pid=$!; trap 'kill \"$forward_pid\" 2>/dev/null || true' EXIT; " +
            "found=0; for i in $(seq 1 30); do " +
            $"if result=$(curl --connect-timeout 2 --max-time 5 -fsS http://localhost:{localPort}{testPath}) && " +
            $"{responseCheck}; then found=1; printf '%s\\n' \"$result\"; break; fi; " +
            "echo \"Attempt $i: got [$result], retrying...\"; sleep 5; done; " +
            "test \"$found\" = 1)",
            counter, TimeSpan.FromMinutes(6));
    }

    /// <summary>
    /// Cleans up a KinD cluster and its owned registry (best-effort, in-terminal).
    /// </summary>
    internal static async Task CleanupKubernetesDeploymentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string clusterName)
    {
        await auto.TypeAsync($"kind delete cluster --name={clusterName} 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Resolve and remove by ID, not name, so a replacement registry is never deleted.
        await auto.TypeAsync(
            "registry_id=$(docker container ls -aq --filter 'name=^/kind-registry$' " +
            $"--filter 'label={KindRegistryOwnerLabel}={clusterName}') && " +
            "if [ -n \"$registry_id\" ]; then docker container rm -f -v \"$registry_id\"; " +
            "else echo 'Cleanup: no registry owned by this test; leaving any reused registry intact.'; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Best-effort out-of-terminal cleanup for finally blocks.
    /// </summary>
    internal static async Task CleanupKindClusterOutOfBandAsync(string clusterName, ITestOutputHelper output)
    {
        // KinD is installed inside the helper container, not necessarily on the test host.
        // Delete only this cluster's nodes through the shared Docker daemon when the terminal
        // has failed; keep the shared kind network intact. Tests using kind-registry run serially.
        await LocalDeploymentTestHelpers.CleanupLabeledDockerResourcesAsync(
            $"io.x-k8s.kind.cluster={clusterName}", output);
        await LocalDeploymentTestHelpers.CleanupLabeledDockerResourcesAsync(
            $"{KindRegistryOwnerLabel}={clusterName}", output);
    }
}
