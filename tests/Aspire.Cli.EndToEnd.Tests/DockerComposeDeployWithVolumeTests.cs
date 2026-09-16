// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E test for <c>aspire deploy</c> to Docker Compose that proves the
/// <c>WithVolume(name, target, env)</c> overload projects correctly for project resources.
///
/// Scenario: a project mounts a named volume and only ever learns the mount path from
/// <c>DATA_PATH</c>. The test asserts the generated compose file and then the *running*
/// container, because generation alone cannot show that the environment variable and the
/// volume actually reach the deployed workload.
///
/// The obvious next assertion — write a file, force-recreate the container, read it back —
/// is deliberately absent. It cannot pass today: .NET images run as a non-root user while a
/// fresh Docker named volume is created root-owned, so the app gets "Permission denied" on
/// its own volume. Kubernetes avoids this by setting fsGroup (see KubernetesResource); Compose
/// has no equivalent. Tracked by https://github.com/microsoft/aspire/issues/19422 — add the
/// durability round-trip here once that is fixed.
///
/// This is the Compose counterpart to <see cref="KubernetesDeployWithProjectPersistentVolumeTests"/>.
/// Both run on every PR because neither needs a cloud subscription.
/// </summary>
public sealed class DockerComposeDeployWithVolumeTests(ITestOutputHelper output)
{
    private const string ProjectName = "ComposeDeployVolumeTest";
    private const string VolumeName = "serverdata";
    private const string MountPath = "/data";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [CaptureWorkspaceOnFailure]
    public async Task DeployComposeWithProjectVolumeMountsNamedVolumeAtEnvPath(bool useProjectV2)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var deploymentId = $"compose-{Guid.NewGuid():N}";
        var serverName = $"server-{deploymentId}";
        var webName = $"web-{deploymentId}";
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
        var response = $"PASSED: {deploymentId} at {MountPath}";

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        // The project never names the mount path itself — it only knows DATA_PATH. That is the
        // whole point of the overload: the same source works in run mode (where the path is an
        // Aspire store directory on the host) and after publish (where it is MountPath).
        var appHostCode = $$"""
            #pragma warning disable ASPIREDOTNETPROJECT001
            #pragma warning disable ASPIREPIPELINES003
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddDockerComposeEnvironment("compose")
                .ConfigureComposeFile(compose =>
                {
                    foreach (var service in compose.Services.Values)
                    {
                        service.Labels["{{LocalDeploymentTestHelpers.DeploymentLabel}}"] = "{{deploymentId}}";
                    }
                    foreach (var volume in compose.Volumes.Values)
                    {
                        volume.Labels["{{LocalDeploymentTestHelpers.DeploymentLabel}}"] = "{{deploymentId}}";
                    }
                    foreach (var network in compose.Networks.Values)
                    {
                        network.Labels["{{LocalDeploymentTestHelpers.DeploymentLabel}}"] = "{{deploymentId}}";
                    }
                });

            var api = {{(useProjectV2
                ? $"builder.AddDotnetProject(\"{serverName}\", \"../{ProjectName}.ApiService/{ProjectName}.ApiService.csproj\")"
                : $"builder.AddProject<Projects.{ProjectName}_ApiService>(\"{serverName}\")")}}
                .WithRemoteImageName("{{deploymentId}}/server")
                .WithVolume("{{VolumeName}}", "{{MountPath}}", env: "DATA_PATH")
                .WithExternalHttpEndpoints();

            {{(useProjectV2
                ? $"builder.AddDotnetProject(\"{webName}\", \"../{ProjectName}.Web/{ProjectName}.Web.csproj\")"
                : $"builder.AddProject<Projects.{ProjectName}_Web>(\"{webName}\")")}}
                .WithRemoteImageName("{{deploymentId}}/web")
                .WithReference(api)
                .WaitFor(api)
                .WithExternalHttpEndpoints();

            builder.Build().Run();
            """;

        // Throwing at startup turns a missing projection into a container that never reaches a
        // running state, so the container lookup below fails loudly instead of the test quietly
        // asserting against a workload that ignored DATA_PATH.
        var apiProgramCode = $$"""
            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();

            var app = builder.Build();
            app.MapDefaultEndpoints();

            var dataPath = Environment.GetEnvironmentVariable("DATA_PATH")
                ?? throw new InvalidOperationException("DATA_PATH is not configured.");

            app.MapGet("/data-path", () => dataPath);
            app.MapGet("/test-deployment", () => Results.Text("PASSED: {{deploymentId}} at " + dataPath));

            app.Run();
            """;

        await auto.ScaffoldK8sDeployProjectAsync(
            counter,
            ProjectName,
            projectDir,
            appHostHostingPackages: ["Aspire.Hosting.Docker", "Aspire.Hosting.Dotnet"],
            apiClientPackages: [],
            appHostCode: appHostCode,
            apiProgramCode: apiProgramCode,
            output: output);

        LocalDeploymentTestHelpers.WriteReferencingWebProgram(projectDir, ProjectName, serverName);

        try
        {
            // ASPIRE_PLAYGROUND forces interactive displays even with --non-interactive.
            await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND; mkdir -p deploy-output", counter);
            await auto.RunCommandAsync(
                "aspire deploy -o deploy-output --non-interactive", counter, TimeSpan.FromMinutes(10));

            // Read Compose's normalized model so the dependency assertion pins the consumer,
            // not just the presence of a depends_on string somewhere in the generated YAML.
            await auto.RunCommandAsync(
                "env_file=$(find deploy-output -maxdepth 1 -name '.env.*' -print); " +
                "test -f \"$env_file\" && docker compose --env-file \"$env_file\" " +
                "-f deploy-output/docker-compose.yaml config --format json > deploy-output/compose.json",
                counter);
            using var compose = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDir, "deploy-output", "compose.json")));
            var services = compose.RootElement.GetProperty("services");
            Assert.Equal("service_started", services.GetProperty(webName).GetProperty("depends_on").GetProperty(serverName).GetProperty("condition").GetString());
            var server = services.GetProperty(serverName);
            Assert.Equal(MountPath, server.GetProperty("environment").GetProperty("DATA_PATH").GetString());
            var volume = Assert.Single(server.GetProperty("volumes").EnumerateArray());
            Assert.Equal("volume", volume.GetProperty("type").GetString());
            Assert.Equal(VolumeName, volume.GetProperty("source").GetString());
            Assert.Equal(MountPath, volume.GetProperty("target").GetString());

            // Unique resource names isolate the default local build tags; labels scope runtime
            // discovery and cleanup. Also prove the containers run our newly built images.
            foreach (var service in new[] { serverName, webName })
            {
                await auto.RunCommandAsync(
                    $"id=$(docker ps -q --filter 'label={LocalDeploymentTestHelpers.DeploymentLabel}={deploymentId}' " +
                    $"--filter 'label=com.docker.compose.service={service}'); test -n \"$id\" && " +
                    "test \"$(docker inspect -f '{{.Image}}' \"$id\")\" = " +
                    $"\"$(docker image inspect -f '{{{{.Id}}}}' '{service}:latest')\"",
                    counter);
            }

            await auto.RunCommandAsync(
                $"id=$(docker ps -q --filter 'label={LocalDeploymentTestHelpers.DeploymentLabel}={deploymentId}' " +
                $"--filter 'label=com.docker.compose.service={serverName}'); " +
                $"test \"$(docker exec \"$id\" printenv DATA_PATH)\" = '{MountPath}' && " +
                "docker inspect -f '{{range .Mounts}}{{.Type}}|{{.Name}}|{{.Destination}}{{println}}{{end}}' \"$id\" " +
                $"| grep -E '^volume\\|.*_{VolumeName}\\|{MountPath}$'",
                counter);

            await VerifyResponseAsync(auto, counter, deploymentId, serverName, "/data-path", MountPath);
            await VerifyResponseAsync(auto, counter, deploymentId, webName, "/test-deployment", $"WEB->{response}");
        }
        finally
        {
            try
            {
                await auto.AspireDestroyAsync(counter);
            }
            catch (Exception ex)
            {
                output.WriteLine($"Destroy failed; removing this deployment's labeled resources: {ex.Message}");
            }

            await LocalDeploymentTestHelpers.CleanupLabeledDockerResourcesAsync(
                $"{LocalDeploymentTestHelpers.DeploymentLabel}={deploymentId}", output);
            await LocalDeploymentTestHelpers.CleanupImageAsync($"{deploymentId}/server:latest", output);
            await LocalDeploymentTestHelpers.CleanupImageAsync($"{deploymentId}/web:latest", output);
            await LocalDeploymentTestHelpers.CleanupImageAsync($"{serverName}:latest", output);
            await LocalDeploymentTestHelpers.CleanupImageAsync($"{webName}:latest", output);
        }
    }

    private static async Task VerifyResponseAsync(
        Hex1bTerminalAutomator auto, SequenceCounter counter, string deploymentId,
        string service, string path, string expectedResponse)
    {
        // The host Docker daemon owns the workload network. Probe inside that namespace
        // rather than assuming the published host port is reachable from the helper container.
        await auto.RunCommandAsync(
            $"id=$(docker ps -q --filter 'label={LocalDeploymentTestHelpers.DeploymentLabel}={deploymentId}' " +
            $"--filter 'label=com.docker.compose.service={service}'); test -n \"$id\" && " +
            "{ found=0; for i in $(seq 1 20); do " +
            $"if result=$(docker run --rm --label '{LocalDeploymentTestHelpers.DeploymentLabel}={deploymentId}' " +
            "--network container:$id curlimages/curl:8.12.1 --connect-timeout 2 --max-time 5 -fsS " +
            $"http://localhost:8080{path}) && [ \"$result\" = '{expectedResponse}' ]; " +
            "then found=1; printf '%s\\n' \"$result\"; break; fi; sleep 3; done; test \"$found\" = 1; }",
            counter, TimeSpan.FromMinutes(4));
    }
}
