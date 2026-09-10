// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes: DeployK8sBasicApiService.
/// </summary>
public sealed class KubernetesDeployBasicApiServiceTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployTest";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sBasicApiService(bool useProjectV2)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";
        var serverName = $"server-{clusterName}";
        var imageName = $"{clusterName}/server";
        var imageTag = useProjectV2 ? "project-v2" : "legacy";
        var expectedImage = $"localhost:5001/{imageName}:{imageTag}";
        var response = $"PASSED: basic API service is running ({clusterName}, {imageTag})";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        // Prepare environment
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
            // Phase 2: Scaffold the project on disk
            // =====================================================================

            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
                #pragma warning disable ASPIREDOTNETPROJECT001
                #pragma warning disable ASPIREPIPELINES003
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var api = {{(useProjectV2
                    ? $"builder.AddDotnetProject(\"{serverName}\", \"../{ProjectName}.ApiService/{ProjectName}.ApiService.csproj\")"
                    : $"builder.AddProject<Projects.{ProjectName}_ApiService>(\"{serverName}\")")}}
                    .WithRemoteImageName("{{imageName}}")
                    .WithRemoteImageTag("{{imageTag}}")
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = $$"""
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", () =>
                {
                    return Results.Text("{{response}}");
                });

                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes", "Aspire.Hosting.Dotnet"],
                apiClientPackages: [],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);

            // =====================================================================
            // Phase 3: Run aspire deploy interactively
            // =====================================================================

            // The deploy will prompt for parameters in code declaration order:
            // 1. registryendpoint - the container registry (localhost:5001 for KinD local registry)
            // 2. namespace - the K8s namespace
            // 3. chartversion - the Helm chart version
            await auto.AspireDeployInteractiveAsync(
                counter,
                parameterResponses:
                [
                    ("registryendpoint", "localhost:5001"),
                    ("namespace", k8sNamespace),
                    ("chartversion", "0.1.0"),
                ]);

            // =====================================================================
            // Phase 4: Verify the deployment
            // =====================================================================

            await auto.VerifyPodImageAsync(counter, k8sNamespace, $"app.kubernetes.io/component={serverName}", expectedImage);
            await auto.VerifyDeploymentAsync(
                counter,
                @namespace: k8sNamespace,
                serviceName: serverName,
                localPort: 18080,
                testPath: "/test-deployment",
                expectedResponse: response);

            // =====================================================================
            // Phase 5: Cleanup
            // =====================================================================

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
            await LocalDeploymentTestHelpers.CleanupImageAsync(expectedImage, output);
            await LocalDeploymentTestHelpers.CleanupImageAsync($"{serverName}:latest", output);
        }
    }
}
