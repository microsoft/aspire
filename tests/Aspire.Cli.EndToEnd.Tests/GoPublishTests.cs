// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI publish flows with a Go AppHost.
/// </summary>
public sealed class GoPublishTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithDockerComposeServiceCallbackSucceeds()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.AspireInitGoAppHostAsync(counter);
        await auto.AspireAddPackageAsync("Aspire.Hosting.Docker", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        WriteDockerComposeContainerAppHost(workspace);

        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.RunCommandAsync("aspire publish -o artifacts --non-interactive", counter, TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync("ls -la artifacts/docker-compose.yaml", counter);
        await auto.RunCommandAsync("grep -F \"api:\" artifacts/docker-compose.yaml", counter);
        await auto.RunCommandAsync("grep -F \"validation-api\" artifacts/docker-compose.yaml", counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithoutOutputPathUsesAppHostDirectoryDefault()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.AspireInitGoAppHostAsync(counter);
        await auto.AspireAddPackageAsync("Aspire.Hosting.Docker", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        WriteDockerComposeContainerAppHost(workspace);

        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.RunCommandAsync("aspire publish --non-interactive", counter, TimeSpan.FromMinutes(5));

        var dockerComposePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-output", "docker-compose.yaml");
        Assert.True(File.Exists(dockerComposePath), $"Expected docker-compose output at {dockerComposePath}");

        var dockerComposeContent = await File.ReadAllTextAsync(dockerComposePath);
        Assert.Contains("api:", dockerComposeContent);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishGoStarterGeneratesGoAppDockerfile()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        const string projectName = "GoPublishApp";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.GoStarter);
        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.AspireAddPackageAsync("Aspire.Hosting.Docker", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        // The starter has no compute environment; the Go API and the Redis cache both target this one.
        GoAppHostTestHelpers.InsertBeforeBuild(
            Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.go"),
            "builder.AddDockerComposeEnvironment(\"compose\")");

        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.RunCommandAsync("aspire publish -o artifacts --non-interactive", counter, TimeSpan.FromMinutes(5));

        var artifactsPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "artifacts");
        var composeContent = await File.ReadAllTextAsync(Path.Combine(artifactsPath, "docker-compose.yaml"));

        Assert.Contains("api:", composeContent);
        Assert.Contains("cache:", composeContent);

        // The Go version in the FROM line comes from api/go.mod, so only the stable parts are asserted.
        AssertDockerfileContains(
            artifactsPath,
            "api",
            "FROM golang:",
            " AS build",
            "USER app",
            "ENTRYPOINT [\"/app/api\"]");
    }

    private static void WriteDockerComposeContainerAppHost(TemporaryWorkspace workspace)
    {
        GoAppHostTestHelpers.InsertBeforeBuild(
            Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.go"),
            """
            compose := builder.AddDockerComposeEnvironment("compose")
            api := builder.AddContainer("api", "nginx:alpine")
            api.WithComputeEnvironment(compose)
            _ = api.PublishAsDockerComposeService(func(composeService aspire.DockerComposeServiceResource, service aspire.Service) {
                service.SetContainerName(aspire.StringPtr("validation-api"))
            })
            """);
    }

    private static void AssertDockerfileContains(string artifactsPath, string resourceName, params string[] expectedFragments)
    {
        var dockerfilePath = Path.Combine(artifactsPath, $"{resourceName}.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Expected Dockerfile for resource '{resourceName}' at {dockerfilePath}");

        var content = File.ReadAllText(dockerfilePath);

        foreach (var expectedFragment in expectedFragments)
        {
            Assert.Contains(expectedFragment, content);
        }
    }
}
