// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes with a Go AppHost created from the starter template.
/// Creates a Go API + Redis project, adds Kubernetes support, and deploys to a KinD cluster.
/// </summary>
public sealed class GoKubernetesDeployTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sGoTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployGoAppToKubernetes()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
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
            // Phase 2: Create Go project from template
            // =====================================================================

            await auto.AspireNewAsync(ProjectName, counter, template: AspireTemplate.GoStarter);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Add Kubernetes hosting package
            await auto.AspireAddPackageAsync("Aspire.Hosting.Kubernetes", counter);

            // Regenerate the Go SDK with Kubernetes types
            await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

            // =====================================================================
            // Phase 3: Modify apphost.go to add Kubernetes environment
            // =====================================================================

            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "apphost.go");

            output.WriteLine($"Looking for apphost.go at: {appHostFilePath}");

            GoAppHostTestHelpers.InsertBeforeBuild(
                appHostFilePath,
                """
                // Add container registry for image push
                registryEndpoint := builder.AddParameter("registryendpoint")
                builder.AddContainerRegistry("registry", registryEndpoint)

                namespace := builder.AddParameter("namespace")
                chartVersion := builder.AddParameter("chartversion")
                builder.AddKubernetesEnvironment("env").WithHelm(aspire.HelmConfigure(
                    func(helm aspire.HelmChartOptions) {
                        helm.WithNamespace(namespace)
                        helm.WithChartVersion(chartVersion)
                    },
                ))
                """);
            output.WriteLine("Modified apphost.go with Kubernetes environment configuration");

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
            // Phase 5: Verify the deployment
            // =====================================================================

            // The publisher labels each workload and its pods with app.kubernetes.io/component=<AppHost resource name>.
            // Selecting by that label makes a chart without the workload fail the test: "kubectl wait" exits 1 with
            // "no matching resources found" when nothing matches, whereas "kubectl get pods" succeeds on an empty
            // namespace and Helm's --wait only waits for workloads that are in the chart.
            foreach (var resourceName in new[] { "api", "cache" })
            {
                await auto.RunCommandAsync(
                    $"kubectl wait --for=condition=Ready pod -n {k8sNamespace} -l app.kubernetes.io/component={resourceName} --timeout=180s",
                    counter,
                    TimeSpan.FromMinutes(4));
            }

            // The frontend is a build-only resource whose output is copied into the API container.
            // A separate workload would expose the wrong production model and duplicate the public surface.
            await auto.RunCommandAsync(
                $"test -z \"$(kubectl get deployments,pods,services -n {k8sNamespace} -l app.kubernetes.io/component=frontend -o name)\"",
                counter);

            // Verify the API container serves the SPA, one of its compiled assets, and the API endpoint
            // from the same origin. Vite emits the entry script as:
            //   <script type="module" crossorigin src="/assets/index-<hash>.js"></script>
            await auto.RunCommandAsync(
                $$"""
                (service_port=$(kubectl get service api-service -n {{k8sNamespace}} -o jsonpath='{.spec.ports[0].port}'); \
                kubectl port-forward -n {{k8sNamespace}} service/api-service 18081:$service_port > port-forward.log 2>&1 & \
                forward_pid=$!; trap 'kill "$forward_pid" 2>/dev/null || true' EXIT; \
                found=0; for i in $(seq 1 30); do \
                if curl --connect-timeout 2 --max-time 5 -fsS http://localhost:18081/ -o index.html && \
                asset=$(node -e 'const fs = require("node:fs"); const html = fs.readFileSync("index.html", "utf8"); const match = html.match(/<script[^>]+src="([^"]+\.js)"/); if (!match) process.exit(1); process.stdout.write(match[1]);') && \
                curl --connect-timeout 2 --max-time 5 -fsS "http://localhost:18081$asset" -o frontend.js && \
                test -s frontend.js && \
                curl --connect-timeout 2 --max-time 5 -fsS http://localhost:18081/api/weatherforecast -o weather.json && \
                node -e 'const assert = require("node:assert/strict"); const weather = require("./weather.json"); assert.equal(weather.length, 5); for (const forecast of weather) { assert.equal(typeof forecast.temperatureC, "number"); assert.equal(typeof forecast.summary, "string"); }'; \
                then found=1; break; fi; \
                echo "Attempt $i: deployed Go starter is not ready, retrying..."; sleep 5; done; \
                test "$found" = 1)
                """,
                counter,
                TimeSpan.FromMinutes(6));

            // =====================================================================
            // Phase 6: Cleanup
            // =====================================================================

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
