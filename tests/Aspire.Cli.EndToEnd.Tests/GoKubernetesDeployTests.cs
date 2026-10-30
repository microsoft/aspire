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
        const string k8sNamespace = "default";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.VerifyPullRequestCliVersionAsync(counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

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

            // The starter ships api/go.mod without a go.sum, and its "go mod tidy" resource only runs
            // under "aspire run". The generated Dockerfile builds from go.mod and go.sum alone, so
            // the container build needs the go.sum that "go mod tidy" writes.
            await auto.RunCommandAsync("(cd api && go mod tidy)", counter, TimeSpan.FromMinutes(3));

            // =====================================================================
            // Phase 3: Modify apphost.go to add Kubernetes environment
            // =====================================================================

            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "apphost.go");

            output.WriteLine($"Looking for apphost.go at: {appHostFilePath}");

            // The AppHost passes no WithHelm callback: a Configure func inside the generated Go options
            // struct panics the options merge (https://github.com/microsoft/aspire/issues/18080), so the
            // Helm defaults apply (namespace "default", chart version 0.1.0).
            GoAppHostTestHelpers.InsertBeforeBuild(
                appHostFilePath,
                """
                // Add container registry for image push
                registryEndpoint := builder.AddParameter("registryendpoint")
                builder.AddContainerRegistry("registry", registryEndpoint)

                // Add Kubernetes environment (Helm is the default deployment engine)
                builder.AddKubernetesEnvironment("env")
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
