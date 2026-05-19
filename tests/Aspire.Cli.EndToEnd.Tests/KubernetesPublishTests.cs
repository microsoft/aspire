// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI publishing to Kubernetes/Helm.
/// Tests the complete workflow: create project, add Kubernetes integration, publish, generate Helm chart,
/// deploy to a local KinD cluster, and verify the deployment.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class KubernetesPublishTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireKubernetesPublishTest";
    private const string ClusterNamePrefix = "aspire-e2e";

    private static string KindVersion => Environment.GetEnvironmentVariable("KIND_VERSION") ?? "v0.31.0";
    private static string HelmVersion => Environment.GetEnvironmentVariable("HELM_VERSION") ?? "v3.17.3";

    private static string GenerateUniqueClusterName() =>
        $"{ClusterNamePrefix}-{Guid.NewGuid():N}"[..32]; // KinD cluster names max 32 chars

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15930")]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/15870")]
    public async Task CreateAndPublishToKubernetes()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = GenerateUniqueClusterName();

        output.WriteLine($"Using KinD version: {KindVersion}");
        output.WriteLine($"Using Helm version: {HelmVersion}");
        output.WriteLine($"Using cluster name: {clusterName}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            // =====================================================================
            // Phase 1: Install KinD and Helm tools
            // =====================================================================

            // Install kind to ~/.local/bin (no sudo required)
            await auto.TypeAsync("mkdir -p ~/.local/bin");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"curl -sSLo ~/.local/bin/kind \"https://github.com/kubernetes-sigs/kind/releases/download/{KindVersion}/kind-linux-amd64\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("chmod +x ~/.local/bin/kind");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Install helm to ~/.local/bin
            await auto.TypeAsync($"curl -sSL https://get.helm.sh/helm-{HelmVersion}-linux-amd64.tar.gz | tar xz -C /tmp");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("mv /tmp/linux-amd64/helm ~/.local/bin/helm && rm -rf /tmp/linux-amd64");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Add ~/.local/bin to PATH for this session
            await auto.TypeAsync("export PATH=\"$HOME/.local/bin:$PATH\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Verify installations
            await auto.TypeAsync("kind version && helm version --short");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 2: Create KinD cluster
            // =====================================================================

            // Delete any existing cluster with the same name to ensure a clean state
            await auto.TypeAsync($"kind delete cluster --name={clusterName} || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync($"kind create cluster --name={clusterName} --wait=120s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Verify cluster is ready
            await auto.TypeAsync($"kubectl cluster-info --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("kubectl get nodes");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 3: Create Aspire project and generate Helm chart
            // =====================================================================

            // Step 1: Create a new Aspire Starter App
            await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

            // Step 2: Navigate into the project directory
            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 3: Add Aspire.Hosting.Kubernetes package using aspire add
            // Pass the package name directly as an argument to avoid interactive selection
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            // Step 4: Modify AppHost's main file to add Kubernetes environment
            // Note: Aspire templates use AppHost.cs as the main entry point, not Program.cs
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Insert the Kubernetes environment before builder.Build().Run();
            var buildRunPattern = "builder.Build().Run();";
            var replacement = """
// Add Kubernetes environment for Helm chart generation
builder.AddKubernetesEnvironment("env");

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 5: Create output directory for Helm chart artifacts
            await auto.TypeAsync("mkdir -p helm-output");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 6: Unset ASPIRE_PLAYGROUND before publish
            // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive in CliHostEnvironment,
            // which causes Spectre.Console to try to show interactive spinners and prompts concurrently,
            // resulting in "Operations with dynamic displays cannot run at the same time" errors.
            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 7: Run aspire publish to generate Helm charts
            // This will build the project and generate Kubernetes manifests as Helm charts
            // Use --non-interactive to avoid any prompts during publishing
            await auto.TypeAsync("aspire publish -o helm-output --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 8: Verify the Helm chart files were generated
            // Check for Chart.yaml (required Helm chart metadata)
            await auto.TypeAsync("cat helm-output/Chart.yaml");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Verify values.yaml exists (Helm values file)
            await auto.TypeAsync("cat helm-output/values.yaml");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Verify templates directory exists with Kubernetes manifests
            await auto.TypeAsync("ls -la helm-output/templates/");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Display the directory structure for debugging
            await auto.TypeAsync("find helm-output -type f | head -20");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 4: Build container images using aspire do build
            // =====================================================================

            // Build container images for the projects using the Aspire pipeline
            // This uses dotnet publish /t:PublishContainer to build images locally
            await auto.TypeAsync("aspire do build --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // List the built Docker images to verify they exist
            // The Starter App builds: apiservice:latest and webfrontend:latest
            await auto.TypeAsync("docker images | grep -E 'apiservice|webfrontend'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Load the built images into the KinD cluster
            // KinD runs containers inside Docker, so we need to load images into the cluster's nodes
            await auto.TypeAsync($"kind load docker-image apiservice:latest --name={clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync($"kind load docker-image webfrontend:latest --name={clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // =====================================================================
            // Phase 5: Deploy the Helm chart to KinD cluster
            // =====================================================================

            // Validate the Helm chart before installing
            await auto.TypeAsync("helm lint helm-output");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Perform a dry-run first to catch any issues
            await auto.TypeAsync("helm install aspire-app helm-output --dry-run");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Show the image and port parameters from values.yaml for debugging
            await auto.TypeAsync("cat helm-output/values.yaml | grep -E '_image:|port_'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Install the Helm chart using the real container images built by Aspire
            // The images are already loaded into KinD, so we use the default values.yaml
            // which references apiservice:latest and webfrontend:latest
            // Override ports to ensure unique values per service - the Helm chart may have
            // duplicate port defaults that cause "port already allocated" errors during deployment
            await auto.TypeAsync("helm install aspire-app helm-output " +
                "--set parameters.apiservice.port_http=8080 " +
                "--set parameters.apiservice.port_https=8443 " +
                "--set parameters.webfrontend.port_http=8081 " +
                "--set parameters.webfrontend.port_https=8444 " +
                "--wait --timeout 3m");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // Verify the Helm release was created and is deployed
            await auto.TypeAsync("helm list");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Check that pods are running
            await auto.TypeAsync("kubectl get pods");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Wait for all pods to be ready (not just created)
            await auto.TypeAsync("kubectl wait --for=condition=Ready pod --all --timeout=120s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Check all Kubernetes resources were created
            await auto.TypeAsync("kubectl get all");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Show the deployed configmaps and secrets
            await auto.TypeAsync("kubectl get configmaps,secrets");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 6: Cleanup
            // =====================================================================

            // Destroy the deployment using aspire destroy (runs helm uninstall)
            await auto.AspireDestroyAsync(counter);

            // Delete the KinD cluster
            await auto.TypeAsync($"kind delete cluster --name={clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
        }
        finally
        {
            // Best-effort cleanup: ensure cluster is deleted even if test fails
            // This runs outside the terminal sequence to guarantee execution
            try
            {
                using var cleanupProcess = new System.Diagnostics.Process();
                cleanupProcess.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "kind");
                cleanupProcess.StartInfo.Arguments = $"delete cluster --name={clusterName}";
                cleanupProcess.StartInfo.RedirectStandardOutput = true;
                cleanupProcess.StartInfo.RedirectStandardError = true;
                cleanupProcess.StartInfo.UseShellExecute = false;
                cleanupProcess.Start();
                await cleanupProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
                output.WriteLine($"Cleanup: KinD cluster '{clusterName}' deleted (exit code: {cleanupProcess.ExitCode})");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Cleanup: Failed to delete KinD cluster '{clusterName}': {ex.Message}");
            }
        }

        await pendingRun;
    }
}
