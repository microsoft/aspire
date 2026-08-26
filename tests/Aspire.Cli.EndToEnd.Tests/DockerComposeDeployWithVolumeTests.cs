// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployComposeWithProjectVolumeMountsNamedVolumeAtEnvPath()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

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
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddDockerComposeEnvironment("compose");

            builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                .WithVolume("{{VolumeName}}", "{{MountPath}}", env: "DATA_PATH")
                .WithExternalHttpEndpoints();

            builder.Build().Run();
            """;

        // Throwing at startup turns a missing projection into a container that never reaches a
        // running state, so the container lookup below fails loudly instead of the test quietly
        // asserting against a workload that ignored DATA_PATH.
        var apiProgramCode = """
            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();

            var app = builder.Build();
            app.MapDefaultEndpoints();

            var dataPath = Environment.GetEnvironmentVariable("DATA_PATH")
                ?? throw new InvalidOperationException("DATA_PATH is not configured.");

            app.MapGet("/data-path", () => dataPath);

            app.Run();
            """;

        await auto.ScaffoldK8sDeployProjectAsync(
            counter,
            ProjectName,
            Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName),
            appHostHostingPackages: ["Aspire.Hosting.Docker"],
            apiClientPackages: [],
            appHostCode: appHostCode,
            apiProgramCode: apiProgramCode,
            output: output);

        await auto.TypeAsync("mkdir -p deploy-output");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive in CliHostEnvironment,
        // which causes Spectre.Console to try to show interactive spinners and prompts concurrently,
        // resulting in "Operations with dynamic displays cannot run at the same time" errors.
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire deploy -o deploy-output --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

        // === Verify the published projection ===
        // DATA_PATH must carry the published target (not a host path), and the mount must be
        // backed by a named volume rather than a bind mount, which is what makes the storage
        // outlive the container.
        output.WriteLine("Verify: compose file projects DATA_PATH and a named volume");
        // The grep chain alone sets the exit status. An `|| echo ...BAD` fallback would swallow a
        // failed grep behind a zero exit, so WaitForSuccessPromptAsync (which reads the prompt's
        // OK/ERR marker) would pass on a broken compose file.
        //
        // The sentinel is split so only the shell can assemble it: the typed line reads
        // COMPOSE_SHAPE$(echo _OK) while the executed echo emits COMPOSE_SHAPE_OK. Otherwise
        // WaitUntilTextAsync matches the echoed command still on screen and passes regardless of
        // the greps. Same idiom as KubernetesDeployWithPersistentVolumeTests.
        await auto.TypeAsync(
            $"grep -q 'DATA_PATH: \"{MountPath}\"' deploy-output/docker-compose.yaml && " +
            $"grep -q 'source: \"{VolumeName}\"' deploy-output/docker-compose.yaml && " +
            $"grep -q 'type: \"volume\"' deploy-output/docker-compose.yaml && " +
            "echo \"COMPOSE_SHAPE$(echo _OK)\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("COMPOSE_SHAPE_OK", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // === Verify the running container ===
        // Compose expands the volume name with a project prefix (aspire-compose-<hash>_serverdata),
        // so match on a substring rather than the literal declared name. Retried because the
        // service can still be starting immediately after deploy reports success.
        output.WriteLine("Verify: running container has DATA_PATH and the named volume mounted there");
        // `found` is what makes the exit status meaningful: an exhausted loop would otherwise end
        // on `sleep 3` and report success. The trailing `test` turns "never matched" into a
        // non-zero exit, and the split sentinel keeps WaitUntilTextAsync from matching the typed
        // command line rather than real output.
        await auto.TypeAsync(
            "found=0; for i in $(seq 1 20); do " +
            "id=$(docker ps --filter 'name=server' --format '{{.ID}}' | head -1); " +
            "if [ -n \"$id\" ]; then " +
            "envval=$(docker exec $id printenv DATA_PATH 2>/dev/null); " +
            "mounts=$(docker inspect -f '{{range .Mounts}}{{.Type}}|{{.Name}}|{{.Destination}} {{end}}' $id 2>/dev/null); " +
            "echo \"DATA_PATH=[$envval] MOUNTS=[$mounts]\"; " +
            $"if [ \"$envval\" = \"{MountPath}\" ] && echo \"$mounts\" | grep -q 'volume|.*{VolumeName}|{MountPath}'; " +
            "then found=1; echo \"RUNTIME$(echo _OK)\"; break; fi; " +
            "fi; " +
            "echo \"Attempt $i: waiting for server container...\"; sleep 3; done; " +
            "test \"$found\" = 1");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("RUNTIME_OK", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        await auto.AspireDestroyAsync(counter);
    }
}
