// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class KubernetesDeployWithFileKeyRefTests(ITestOutputHelper output)
{
    // EnvFiles is enabled by default in 1.35. Pin the node image independently of
    // KinD's default so the scenario continues to exercise a known feature version.
    private const string NodeImage = "kindest/node:v1.35.0@sha256:452d707d4862f52530247495d180205e029056831160e22870e37e3f6c1ac31f";
    private const string ProjectName = "K8sFileKeyRefTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployK8sWithFileKeyRef()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var k8sNamespace = $"test-{clusterName[..16]}";
        var serverName = $"server-{clusterName}";
        var imageName = $"{clusterName}/server";
        var expectedImage = $"localhost:5001/{imageName}:filekeyref";
        var expectedValue = $"generated-{clusterName}";
        var response = $"PASSED: {expectedValue}|second value|optional-unset";

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName, NodeImage);

            var appHostCode = $$"""
                #pragma warning disable ASPIRECOMPUTE003
                #pragma warning disable ASPIREPIPELINES003
                using Aspire.Hosting;
                using Aspire.Hosting.Kubernetes;
                using Aspire.Hosting.Kubernetes.Resources;

                var builder = DistributedApplication.CreateBuilder(args);
                var registry = builder.AddContainerRegistry("registry", builder.AddParameter("registryendpoint"));

                builder.AddProject<Projects.{{ProjectName}}_ApiService>("{{serverName}}")
                    .WithRemoteImageName("{{imageName}}")
                    .WithRemoteImageTag("filekeyref")
                    .WithExternalHttpEndpoints()
                    .PublishAsKubernetesService(resource =>
                    {
                        var pod = resource.Workload!.PodTemplate.Spec;
                        pod.Volumes.Add(new VolumeV1 { Name = "generated-env", EmptyDir = new() });
                        pod.InitContainers.Add(new ContainerV1
                        {
                            Name = "prepare-env",
                            Image = "docker.io/library/busybox:1.37@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e",
                            Command = { "sh", "-ec" },
                            Args = { "printf \"SOURCE_KEY='{{expectedValue}}'\\nSECOND_KEY='second value'\\n\" > /out/app.env" },
                            VolumeMounts = { new VolumeMountV1 { Name = "generated-env", MountPath = "/out" } }
                        });
                        foreach (var (name, key, optional) in new[]
                        {
                            ("APP_VALUE", "SOURCE_KEY", false),
                            ("SECOND_VALUE", "SECOND_KEY", false),
                            ("OPTIONAL_VALUE", "MISSING_KEY", true)
                        })
                        {
                            pod.Containers[0].Env.Add(new EnvVarV1
                            {
                                Name = name,
                                ValueFrom = new EnvVarSourceV1
                                {
                                    FileKeyRef = new FileKeySelectorV1
                                    {
                                        VolumeName = "generated-env",
                                        Path = "app.env",
                                        Key = key,
                                        Optional = optional
                                    }
                                }
                            });
                        }
                    });

                builder.AddKubernetesEnvironment("env")
                    .WithDashboard(false)
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
                    var first = Environment.GetEnvironmentVariable("APP_VALUE");
                    var second = Environment.GetEnvironmentVariable("SECOND_VALUE");
                    var optional = Environment.GetEnvironmentVariable("OPTIONAL_VALUE");
                    return Results.Text($"PASSED: {first}|{second}|{(optional is null ? "optional-unset" : optional)}");
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

            await auto.AspireDeployInteractiveAsync(counter, parameterResponses:
            [
                ("registryendpoint", "localhost:5001"),
                ("namespace", k8sNamespace),
                ("chartversion", "0.1.0")
            ]);

            await auto.VerifyPodImageAsync(counter, k8sNamespace, $"app.kubernetes.io/component={serverName}", expectedImage);
            await auto.VerifyDeploymentAsync(counter, k8sNamespace, serverName, 18080, expectedResponse: response);

            // Inspect the actual pod as well as the response: the consumer must receive
            // values without mounting the producer's file or replacing its entrypoint.
            await auto.RunCommandAsync(
                $"kubectl get pods -n {k8sNamespace} -l app.kubernetes.io/component={serverName} -o json > /tmp/filekeyref-pod.json && " +
                "jq -e '.items | length == 1' /tmp/filekeyref-pod.json && " +
                "jq -e '.items[0] | " +
                "(.status.initContainerStatuses[] | select(.name == \"prepare-env\") | .state.terminated.exitCode == 0) and " +
                "(.spec.containers[0] | " +
                "([.volumeMounts[]? | select(.name == \"generated-env\")] | length == 0) and " +
                "([.command[]?] | length == 0) and " +
                "([.env[] | select(.name == \"APP_VALUE\") | .valueFrom.fileKeyRef] == " +
                "[{\"volumeName\":\"generated-env\",\"path\":\"app.env\",\"key\":\"SOURCE_KEY\",\"optional\":false}]))' /tmp/filekeyref-pod.json",
                counter);

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
