// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Deploys two Project V2 .csproj workloads through Radius to KinD, including a
/// real Web-to-API service-discovery request. Radius's explicit-image contract
/// requires the images to be built and pushed before deployment.
/// </summary>
public sealed class RadiusDotnetProjectDeployTests(ITestOutputHelper output)
{
    private const string ProjectName = "RadiusDotnetDeployTest";
    private const string ImageTag = "project-v2";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployRadiusDotnetProjectsToKind()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var radiusNamespace = $"radius-{clusterName[..16]}";
        var response = $"PASSED: Radius Project V2 {clusterName}";

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
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);
            await auto.InstallRadCliAsync(counter);
            await auto.InstallRadiusControlPlaneAsync(counter, clusterName);

            var appHostCode = $$"""
                #pragma warning disable ASPIREDOTNETPROJECT001
                #pragma warning disable ASPIRERADIUS057
                using Aspire.Hosting;

                var builder = DistributedApplication.CreateBuilder(args);
                builder.AddRadiusEnvironment("radius").WithNamespace("{{radiusNamespace}}");

                var api = builder.AddDotnetProject("api", "../{{ProjectName}}.ApiService/{{ProjectName}}.ApiService.csproj")
                    .WithContainerImage("localhost:5001/{{clusterName}}/api:{{ImageTag}}");
                builder.AddDotnetProject("web", "../{{ProjectName}}.Web/{{ProjectName}}.Web.csproj")
                    .WithContainerImage("localhost:5001/{{clusterName}}/web:{{ImageTag}}")
                    .WithReference(api)
                    .WithExternalHttpEndpoints();

                builder.Build().Run();
                """;

            var apiProgramCode = $$"""
                var builder = WebApplication.CreateBuilder(args);
                builder.AddServiceDefaults();
                var app = builder.Build();
                app.MapDefaultEndpoints();
                app.MapGet("/test-deployment", () => Results.Text("{{response}}"));
                app.Run();
                """;

            await auto.ScaffoldK8sDeployProjectAsync(
                counter, ProjectName, projectDir,
                appHostHostingPackages: ["Aspire.Hosting.Radius", "Aspire.Hosting.Dotnet"],
                apiClientPackages: [],
                appHostCode: appHostCode,
                apiProgramCode: apiProgramCode,
                output: output);
            LocalDeploymentTestHelpers.WriteReferencingWebProgram(projectDir, ProjectName, "api");

            // Radius deliberately does not build/push project images. Publish the actual
            // generated projects to archives, load them into the host Docker daemon, then
            // push through its localhost:5001 registry mapping. A direct SDK registry push
            // would instead target the helper container's unrelated loopback interface.
            await auto.RunCommandAsync("mkdir -p images; unset ASPIRE_PLAYGROUND", counter);
            foreach (var (resourceName, projectSuffix) in new[] { ("api", "ApiService"), ("web", "Web") })
            {
                var image = $"{clusterName}/{resourceName}:{ImageTag}";
                await auto.RunCommandAsync(
                    $"dotnet publish {ProjectName}.{projectSuffix}/{ProjectName}.{projectSuffix}.csproj " +
                    "--configuration Release --os linux /t:PublishContainer " +
                    $"-p:ContainerRepository={clusterName}/{resourceName} -p:ContainerImageTag={ImageTag} " +
                    $"-p:ContainerArchiveOutputPath=\"$PWD/images/{resourceName}.tar\"",
                    counter, TimeSpan.FromMinutes(5));
                await auto.RunCommandAsync(
                    $"docker load -i images/{resourceName}.tar && " +
                    $"docker tag {image} localhost:5001/{image} && docker push localhost:5001/{image}",
                    counter, TimeSpan.FromMinutes(3));
            }

            // Radius.Core requires a pre-existing application namespace.
            await auto.RunCommandAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}", counter);
            await auto.RunCommandAsync(
                "aspire deploy -o radius-output --non-interactive", counter, TimeSpan.FromMinutes(15));

            // Pin the entire deployed application graph to our API and Web, not the existing
            // public-container Radius smoke test or a control-plane pod.
            await auto.RunCommandAsync(
                $"test \"$(kubectl get deployments -n {radiusNamespace} -l radapp.io/application=app " +
                "-o jsonpath='{range .items[*]}{.metadata.labels.radapp\\.io/resource}{\"\\n\"}{end}' | sort)\" = $'api\\nweb'",
                counter);
            foreach (var resource in new[] { "api", "web" })
            {
                await auto.VerifyPodImageAsync(
                    counter, radiusNamespace, $"radapp.io/application=app,radapp.io/resource={resource}",
                    $"localhost:5001/{clusterName}/{resource}:{ImageTag}");
            }

            // Radius's container recipe creates {resource}-{resource} Services; exercising
            // web-web and the injected api-api DNS reference proves both endpoint mappings.
            await auto.VerifyForwardedEndpointAsync(
                counter, radiusNamespace, "svc/web-web", 18080, 8080, "/test-deployment", $"WEB->{response}");
        }
        finally
        {
            try
            {
                await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
            }
            catch (Exception ex)
            {
                output.WriteLine($"In-terminal cleanup failed: {ex.Message}");
            }

            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
            foreach (var resource in new[] { "api", "web" })
            {
                await LocalDeploymentTestHelpers.CleanupImageAsync($"localhost:5001/{clusterName}/{resource}:{ImageTag}", output);
                await LocalDeploymentTestHelpers.CleanupImageAsync($"{clusterName}/{resource}:{ImageTag}", output);
            }
        }
    }
}
