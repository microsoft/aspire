// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Shared helpers for Kubernetes deploy E2E tests that use KinD clusters with a local registry.
/// </summary>
internal static class KubernetesDeployTestHelpers
{
    private static string KindVersion => Environment.GetEnvironmentVariable("KIND_VERSION") ?? "v0.31.0";
    private static string HelmVersion => Environment.GetEnvironmentVariable("HELM_VERSION") ?? "v3.17.3";
    private static string KubectlVersion => Environment.GetEnvironmentVariable("KUBECTL_VERSION") ?? "v1.34.3";

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

        // Download KinD if not already installed — GitHub CDN can transiently return HTML instead of binary
        await auto.TypeAsync($"command -v kind >/dev/null 2>&1 || {{ for i in 1 2 3; do curl -sSLo ~/.local/bin/kind \"https://github.com/kubernetes-sigs/kind/releases/download/{KindVersion}/kind-linux-amd64\" && file ~/.local/bin/kind | grep -q ELF && break; echo \"Retry $i: KinD download failed, retrying in 5s...\"; sleep 5; done && chmod +x ~/.local/bin/kind; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        // Download Helm if not already installed
        await auto.TypeAsync($"command -v helm >/dev/null 2>&1 || {{ for i in 1 2 3; do curl -sSL https://get.helm.sh/helm-{HelmVersion}-linux-amd64.tar.gz | tar xz -C /tmp && test -f /tmp/linux-amd64/helm && break; echo \"Retry $i: Helm download failed, retrying in 5s...\"; sleep 5; done && mv /tmp/linux-amd64/helm ~/.local/bin/helm && rm -rf /tmp/linux-amd64; }}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        // Download kubectl if not already installed
        await auto.TypeAsync($"command -v kubectl >/dev/null 2>&1 || {{ for i in 1 2 3; do curl -sSLo ~/.local/bin/kubectl \"https://dl.k8s.io/release/{KubectlVersion}/bin/linux/amd64/kubectl\" && file ~/.local/bin/kubectl | grep -q ELF && break; echo \"Retry $i: kubectl download failed, retrying in 5s...\"; sleep 5; done && chmod +x ~/.local/bin/kubectl; }}");
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

        // Start or reuse a local Docker registry at localhost:5001
        await auto.TypeAsync("docker inspect -f '{{.State.Running}}' kind-registry 2>/dev/null || docker run -d --restart=always -p 5001:5000 --network bridge --name kind-registry registry:2");
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
    /// Runs <c>aspire --version</c> and asserts the CLI version contains a prerelease suffix (e.g. <c>-dev</c>, <c>-pr.NNNNN</c>).
    /// This ensures the test is running against a development build, not a GA release.
    /// Fails the test if the version does not contain a hyphen (indicating a prerelease suffix).
    /// </summary>
    internal static async Task AssertAspireVersionAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        ITestOutputHelper output)
    {
        // Run aspire --version and assert it contains a prerelease suffix (hyphen) via shell
        await auto.TypeAsync("VER=$(aspire --version 2>/dev/null) && echo \"$VER\" | grep -q '-' && echo \"CLI_VERSION_OK:$VER\" || { echo \"CLI_VERSION_FAIL:$VER\"; false; }");
        await auto.EnterAsync();

        var foundOk = false;
        await auto.WaitUntilAsync(
            snapshot =>
            {
                if (new CellPatternSearcher().Find("CLI_VERSION_OK:").Search(snapshot).Count > 0)
                {
                    foundOk = true;
                    return true;
                }
                return new CellPatternSearcher().Find("CLI_VERSION_FAIL:").Search(snapshot).Count > 0;
            },
            timeout: TimeSpan.FromSeconds(30),
            description: "CLI version prerelease assertion");

        await auto.WaitForAnyPromptAsync(counter);

        Assert.True(foundOk, "Aspire CLI version does not contain a prerelease suffix. Expected a development build (e.g. 13.3.0-dev or 13.3.0-pr.NNNNN).");
        output.WriteLine("✅ CLI version contains prerelease suffix");
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
        await auto.DownAsync(); // Navigate to "No"
        await auto.EnterAsync();

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
        await auto.EnterAsync();
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
            // aspire add shows a version selection prompt — accept the first (latest) version
            await auto.WaitUntilTextAsync("(based on NuGet.config)", timeout: TimeSpan.FromSeconds(60));
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
        }

        // Step 4: Add client NuGet packages to ApiService (--prerelease needed for PR builds)
        foreach (var package in apiClientPackages)
        {
            await auto.TypeAsync($"dotnet add {projectName}.ApiService package {package} --prerelease");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
        }

        // Step 5: Inject custom AppHost.cs and ApiService/Program.cs into the template-created project
        var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
        var apiDir = Path.Combine(projectDir, $"{projectName}.ApiService");

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
        await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(10));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Verifies a K8s deployment by port-forwarding and curling the test endpoint.
    /// Returns the curl output for assertion.
    /// </summary>
    internal static async Task VerifyDeploymentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string @namespace,
        string serviceName,
        int localPort,
        string testPath = "/test-deployment")
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

        // Port-forward in background
        await auto.TypeAsync($"kubectl port-forward -n {@namespace} svc/{serviceName}-service {localPort}:8080 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Brief pause for port-forward to establish
        await auto.TypeAsync("sleep 3");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Curl the test endpoint with retries, looking for "PASSED" in response body.
        // Database containers (Postgres, MySQL, SQL Server) may need 60-120s to fully initialize,
        // so we retry up to 30 times with 5s intervals (150s total).
        await auto.TypeAsync($"for i in $(seq 1 30); do " +
            $"result=$(curl -s -w '\\nHTTP_%{{http_code}}' http://localhost:{localPort}{testPath} 2>/dev/null); " +
            "if echo \"$result\" | grep -q 'PASSED'; then echo \"VERIFY_OK: $result\"; break; fi; " +
            "echo \"Attempt $i: got $result, retrying...\"; sleep 5; done");
        await auto.EnterAsync();

        // Wait for the VERIFY_OK marker to appear
        await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(4));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Kill the port-forward background process
        await auto.TypeAsync("kill %1 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter);
    }

    /// <summary>
    /// Cleans up a KinD cluster and registry (best-effort, in-terminal).
    /// </summary>
    internal static async Task CleanupKubernetesDeploymentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string clusterName)
    {
        await auto.TypeAsync($"kind delete cluster --name={clusterName} 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        await auto.TypeAsync("docker rm -f kind-registry 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Best-effort out-of-terminal cleanup for finally blocks.
    /// </summary>
    internal static async Task CleanupKindClusterOutOfBandAsync(string clusterName, ITestOutputHelper output)
    {
        try
        {
            using var kindProcess = new System.Diagnostics.Process();
            kindProcess.StartInfo.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "kind");
            kindProcess.StartInfo.Arguments = $"delete cluster --name={clusterName}";
            kindProcess.StartInfo.RedirectStandardOutput = true;
            kindProcess.StartInfo.RedirectStandardError = true;
            kindProcess.StartInfo.UseShellExecute = false;
            kindProcess.Start();
            await kindProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            output.WriteLine($"Cleanup: KinD cluster '{clusterName}' deleted (exit code: {kindProcess.ExitCode})");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Cleanup: Failed to delete KinD cluster '{clusterName}': {ex.Message}");
        }

        try
        {
            using var registryProcess = new System.Diagnostics.Process();
            registryProcess.StartInfo.FileName = "docker";
            registryProcess.StartInfo.Arguments = "rm -f kind-registry";
            registryProcess.StartInfo.RedirectStandardOutput = true;
            registryProcess.StartInfo.RedirectStandardError = true;
            registryProcess.StartInfo.UseShellExecute = false;
            registryProcess.Start();
            await registryProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

