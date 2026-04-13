// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes with a TypeScript AppHost created from the starter template.
/// Creates an Express/React project, adds Kubernetes support, and deploys to a KinD cluster.
/// </summary>
public sealed class KubernetesDeployTypeScriptTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sTsTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployTypeScriptAppToKubernetes()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare environment
        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        await auto.AssertAspireVersionAsync(counter, output);

        try
        {
            // =====================================================================
            // Phase 1: Install KinD + Helm, create cluster with local registry
            // =====================================================================

            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);

            // =====================================================================
            // Phase 2: Create TypeScript project from template
            // =====================================================================

            await auto.AspireNewAsync(ProjectName, counter, template: AspireTemplate.ExpressReact);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Add Kubernetes hosting package
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter);

            // Regenerate TypeScript SDK with Kubernetes types
            await auto.TypeAsync("aspire restore");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            // =====================================================================
            // Phase 3: Modify apphost.ts to add Kubernetes environment
            // =====================================================================

            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostFilePath = Path.Combine(projectDir, "apphost.ts");

            output.WriteLine($"Looking for apphost.ts at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // Add Kubernetes environment with Helm configuration before build().run()
            content = content.Replace(
                "await builder.build().run();",
                """
// Add container registry for image push
const registryEndpoint = await builder.addParameter("registryendpoint");
await builder.addContainerRegistry("registry", registryEndpoint);

// Register parameters before using them in the Helm callback
const k8sNamespace = await builder.addParameter("namespace");
const chartVersion = await builder.addParameter("chartversion");

// Add Kubernetes environment with Helm deployment
const k8sEnv = await builder.addKubernetesEnvironment("env");
await k8sEnv.withHelm(async (helm) => {
    await helm.withNamespace(k8sNamespace);
    await helm.withChartVersion(chartVersion);
});

await builder.build().run();
""");

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified apphost.ts with Kubernetes environment configuration");

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

            // Verify pods are running in the namespace
            await auto.TypeAsync($"kubectl get pods -n {k8sNamespace} --no-headers");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

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
