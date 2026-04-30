// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Kubernetes: DeployK8sBasicApiService.
/// </summary>
public sealed class KubernetesDeployBasicApiServiceTests(ITestOutputHelper output)
{
    private const string ProjectName = "K8sDeployTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sBasicApiService()
        => await DeployK8sBasicApiServiceCoreAsync(
            async (auto, counter, k8sNamespace) =>
            {
                await auto.AspireDeployInteractiveAsync(
                    counter,
                    parameterResponses:
                    [
                        ("registryendpoint", "localhost:5001"),
                        ("namespace", k8sNamespace),
                        ("chartversion", "0.1.0"),
                    ]);
            },
            expectSuccess: true);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sBasicApiServiceNonInteractiveWithInputs()
        => await DeployK8sBasicApiServiceCoreAsync(
            (auto, counter, k8sNamespace) => auto.AspireDeployNonInteractiveAsync(
                counter,
                inputs:
                [
                    ("registryendpoint", "localhost:5001"),
                    ("namespace", k8sNamespace),
                    ("chartversion", "0.1.0"),
                ]),
            expectSuccess: true);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sBasicApiServiceNonInteractiveWithoutInputsFails()
        => await DeployK8sBasicApiServiceCoreAsync(
            async (auto, counter, _) =>
            {
                await auto.TypeAsync("aspire deploy --non-interactive");
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveRequiredInputMissing, "'registryendpoint'"), timeout: TimeSpan.FromMinutes(5));
                await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));
            },
            expectSuccess: false);

    private async Task DeployK8sBasicApiServiceCoreAsync(
        Func<Hex1bTerminalAutomator, SequenceCounter, string, Task> deployAsync,
        bool expectSuccess)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Namespace: {k8sNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

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
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;

                var builder = DistributedApplication.CreateBuilder(args);

                var registryEndpoint = builder.AddParameter("registryendpoint");
                var registry = builder.AddContainerRegistry("registry", registryEndpoint);

                var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                    .WithExternalHttpEndpoints();

                builder.AddKubernetesEnvironment("env")
                    .WithHelm(helm =>
                    {
                        helm.WithNamespace(builder.AddParameter("namespace"));
                        helm.WithChartVersion(builder.AddParameter("chartversion"));
                    });

                builder.Build().Run();
                """;

            var apiProgramCode = """
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();

                var app = builder.Build();
                app.MapDefaultEndpoints();

                app.MapGet("/test-deployment", () =>
                {
                    return Results.Ok("PASSED: basic API service is running");
                });

                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter,
                ProjectName,
                Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
                appHostHostingPackages: ["Aspire.Hosting.Kubernetes"],
                apiClientPackages: [],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);

            // =====================================================================
            // Phase 3: Run aspire deploy
            // =====================================================================

            await deployAsync(auto, counter, k8sNamespace);

            if (expectSuccess)
            {
                // =====================================================================
                // Phase 4: Verify the deployment
                // =====================================================================

                await auto.VerifyDeploymentAsync(
                    counter,
                    @namespace: k8sNamespace,
                    serviceName: "server",
                    localPort: 18080,
                    testPath: "/test-deployment");
            }

            // =====================================================================
            // Phase 5: Cleanup
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
