// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for deploying an AppHost that targets a Radius compute
/// environment (see <c>Aspire.Hosting.Radius</c>) all the way to running
/// workloads — <b>without any Azure</b>. Where <see cref="RadiusPublishTests"/>
/// stops at generating <c>app.bicep</c>, this test drives the full CLI path
/// (<c>aspire publish</c> → <c>aspire deploy</c> → <c>rad deploy app.bicep</c>)
/// against a local KinD cluster with the Radius control plane installed, then
/// asserts the container is actually scheduled and serving HTTP.
///
/// This gives per-PR, local coverage of the Radius deploy flow alongside the
/// live Azure/AKS test (<c>Aspire.Deployment.EndToEnd.Tests</c>), which only
/// runs on demand.
///
/// A public image (<c>mcr.microsoft.com/dotnet/samples:aspnetapp</c>) is used
/// so the KinD node pulls it directly from MCR. That intentionally avoids the
/// build-and-push-to-localhost:5001 machinery the Kubernetes deploy tests need:
/// no image build, no registry round-trip, and no reliance on the mounted host
/// Docker daemon for image movement — the single biggest reliability win for a
/// per-PR test. The KinD cluster is still created via
/// <see cref="KubernetesDeployTestHelpers.CreateKindClusterWithRegistryAsync"/>
/// (the registry sits idle) because that helper also performs the critical
/// internal-kubeconfig networking fix that lets the helper container reach the
/// cluster's API server.
/// </summary>
public sealed class RadiusDeployTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireRadiusDeployTest";

    // The container image is public and tagged (not :latest), so Kubernetes uses
    // imagePullPolicy: IfNotPresent and the KinD node pulls it once from MCR.
    private const string ContainerImage = "mcr.microsoft.com/dotnet/samples:aspnetapp";
    private const int ContainerPort = 8080;

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployRadiusContainerToKind()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();

        // The Radius app namespace must be a valid RFC 1123 label (WithNamespace
        // enforces this) and must pre-exist before deploy: the Radius.Core
        // environment controller hard-fails if the target namespace is missing
        // (the UDT environment model, unlike the legacy Applications.Core model,
        // deliberately does not auto-create it).
        var radiusNamespace = $"radius-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Radius namespace: {radiusNamespace}");

        // mountDockerSocket: true is required so KinD (and the Radius control-plane
        // images it pulls) run against the host Docker daemon from inside the
        // helper container.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            // =================================================================
            // Phase 1: Cluster + Radius control plane
            // =================================================================
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);
            await auto.InstallRadCliAsync(counter);
            await auto.InstallRadiusControlPlaneAsync(counter, clusterName);

            // =================================================================
            // Phase 2: Scaffold the AppHost
            // =================================================================

            // Empty AppHost template (not Starter): the Radius publisher fails on
            // ProjectResources with no attached image, so we add exactly one
            // container. This mirrors RadiusPublishTests.
            await auto.AspireNewCSharpEmptyAppHostAsync(ProjectName, counter);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            // Insert the Radius wiring before `builder.Build().Run();`. AddRadiusEnvironment,
            // WithNamespace, AddContainer, and WithHttpEndpoint are all non-[Experimental],
            // so no ASPIRERADIUS*/ASPIREPIPELINES* suppression is needed. WithHttpEndpoint's
            // targetPort drives the container port that the Radius publisher emits (and thus
            // the port of the Service the recipe creates).
            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            const string buildRunPattern = "builder.Build().Run();";
            Assert.Contains(buildRunPattern, content);
            var radiusWiring = $$"""
                builder.AddRadiusEnvironment("radius").WithNamespace("{{radiusNamespace}}");
                builder.AddContainer("web", "{{ContainerImage}}")
                    .WithHttpEndpoint(targetPort: {{ContainerPort}});
                """;
            content = content.Replace(buildRunPattern, radiusWiring + Environment.NewLine + Environment.NewLine + buildRunPattern);
            File.WriteAllText(appHostFilePath, content);

            // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive and makes
            // Spectre.Console attempt concurrent dynamic displays (see KubernetesPublishTests).
            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =================================================================
            // Phase 3: Publish and assert the generated Bicep shape
            // =================================================================
            await auto.TypeAsync("aspire publish -o radius-output --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            var appBicepPath = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "radius-output", "app.bicep");
            Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
            var appBicep = File.ReadAllText(appBicepPath);
            Assert.Contains("Radius.Core/environments", appBicep);
            Assert.Contains("Radius.Compute/containers", appBicep);
            Assert.Contains(ContainerImage, appBicep);

            // =================================================================
            // Phase 4: Create the app namespace, then deploy
            // =================================================================
            await auto.TypeAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // aspire deploy regenerates the artifacts and runs `rad deploy app.bicep`
            // against the radius-e2e workspace (pinned to this KinD cluster). A
            // container-only Radius app has no parameters to prompt for.
            //
            // Wait on this command's own sequence-numbered prompt with the full deploy
            // budget rather than WaitForPipelineSuccessAsync: the latter scans the whole
            // viewport and would match the stale "Pipeline succeeded" left by the earlier
            // `aspire publish`, returning before this deploy finishes. The prompt wait is
            // scoped to this command and still fails fast on a non-zero deploy via the ERR
            // prompt.
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(15));

            // =================================================================
            // Phase 5: Verify the workload is scheduled and serving HTTP
            // =================================================================

            // Radius labels every workload it creates with radapp.io/application and
            // radapp.io/resource; wait on the app label so we don't depend on the
            // generated Deployment/pod name.
            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod -n {radiusNamespace} -l radapp.io/application=app --timeout=180s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            await auto.TypeAsync($"kubectl get pods,svc -n {radiusNamespace} -l radapp.io/application=app");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Resolve the Deployment by the radapp.io/resource label and port-forward to it
            // directly. Radius does not synthesize a Kubernetes Service for a container workload
            // (the HTTP endpoint is modeled at the Radius layer, not as a k8s Service), so there
            // is no Service to target; only the Deployment/pods exist. Resolving by label avoids
            // depending on the generated Deployment name.
            await auto.TypeAsync($"RADIUS_DEPLOY=$(kubectl get deployment -n {radiusNamespace} -l radapp.io/resource=web -o jsonpath='{{.items[0].metadata.name}}') && echo \"Resolved deployment: $RADIUS_DEPLOY\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"kubectl port-forward -n {radiusNamespace} deployment/$RADIUS_DEPLOY 18080:{ContainerPort} &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("sleep 3");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The aspnetapp sample serves HTTP 200 on `/`. Retry to absorb the brief
            // window while the port-forward and container finish coming up. The success
            // marker is split in the shell source (VERIFY''_OK evaluates to VERIFY_OK) so
            // the contiguous token appears only in curl's output on a 200, never in the
            // echoed command line — otherwise WaitUntilTextAsync would match the command
            // itself and return before curl succeeds. Mirrors BICEP_IMAGES''_OK in the
            // AKS deployment test.
            await auto.TypeAsync("for i in $(seq 1 20); do " +
                "code=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:18080/ 2>/dev/null); " +
                "if [ \"$code\" = \"200\" ]; then echo VERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: got http=$code, retrying...\"; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("kill %1 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForAnyPromptAsync(counter);

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
