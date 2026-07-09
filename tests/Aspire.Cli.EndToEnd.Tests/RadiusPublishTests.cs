// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Lightweight end-to-end coverage for publishing an AppHost that targets a
/// Radius compute environment (see <c>Aspire.Hosting.Radius</c>). Unlike the
/// in-proc unit/snapshot tests, this exercises the full CLI → AppHost build →
/// publish-pipeline path, but stays cheap and deterministic: <c>aspire publish</c>
/// stops at generating <c>app.bicep</c> + <c>bicepconfig.json</c>, so no <c>rad</c>
/// CLI and no Kubernetes cluster are required.
///
/// Modeled on <see cref="KubernetesPublishRequiresExternalEndpointTests"/> (the
/// lightest "run aspire publish, assert output artifacts" precedent) rather than
/// <c>KubernetesPublishTests</c>, which additionally spins up KinD + Helm + Docker.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class RadiusPublishTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireRadiusPublishTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithRadiusEnvironment_EmitsExpectedArtifacts()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        // No docker socket mount: publish only generates Bicep, it does not need a
        // container daemon inside the workload (unlike KubernetesPublishTests).
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Use the Empty AppHost template, not the Starter. The Radius publisher
        // fails on ProjectResources that have no attached container image, and the
        // Starter ships projects. The empty template scaffolds a single-file
        // file-based apphost.cs whose only body is `builder.Build().Run();`, so we
        // add exactly the resources we want. AspireNewCSharpEmptyAppHostAsync is used
        // (rather than the generic AspireNewAsync) because it robustly handles the
        // language + agent-init prompts instead of racing on a short language-prompt
        // wait.
        await auto.AspireNewCSharpEmptyAppHostAsync(ProjectName, counter);

        // cd into the project so `aspire add` and `aspire publish` resolve the
        // AppHost via repo-root discovery.
        await auto.TypeAsync($"cd {ProjectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Add the Radius hosting package. `aspire add` resolves the version against
        // the same feed configuration the rest of the CLI uses (including PR builds),
        // matching how KubernetesPublishRequiresExternalEndpointTests adds Kubernetes.
        await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

        // Patch the AppHost in-place. AspireNewCSharpEmptyAppHostAsync scaffolds a
        // single-file, file-based AppHost at `<ProjectName>/apphost.cs` (not a
        // `<ProjectName>.AppHost/AppHost.cs` project — that is the Starter layout).
        // Its body is just `builder.Build().Run();`, so we insert the Radius wiring
        // immediately before it. AddRadiusEnvironment and AddContainer are NOT
        // [Experimental], so the AppHost compiles without any ASPIRERADIUS*
        // suppression (cloud providers and WithContainerImage are experimental and
        // intentionally out of scope). A registry-less image ("nginx") only produces
        // a publisher WARN, not a failure. Failing to find the marker surfaces as a
        // clear test failure rather than a silently no-op publish.
        var appHostFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ProjectName,
            "apphost.cs");
        var content = File.ReadAllText(appHostFilePath);
        const string buildRunPattern = "builder.Build().Run();";
        Assert.Contains(buildRunPattern, content);
        const string radiusWiring = """
            builder.AddRadiusEnvironment("radius");
            builder.AddContainer("web", "nginx");
            """;
        content = content.Replace(buildRunPattern, radiusWiring + Environment.NewLine + Environment.NewLine + buildRunPattern);
        File.WriteAllText(appHostFilePath, content);

        // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive and makes
        // Spectre.Console attempt concurrent dynamic displays. See
        // KubernetesPublishTests for full context.
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Drive aspire publish. On success the pipeline exits 0 and
        // WaitForSuccessPromptAsync matches the ` OK] $ ` prompt (it throws on an
        // ` ERR:` prompt), so this both waits for completion and asserts exit 0.
        await auto.TypeAsync("aspire publish -o radius-output --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Assert the emitted artifact shape (smoke-level, not golden-file). With a
        // single compute environment the publisher writes flat root output, so we
        // assert exact paths rather than searching recursively — a nested or
        // mislocated layout should fail the test.
        var outputDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "radius-output");

        var appBicepPath = Path.Combine(outputDir, "app.bicep");
        Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
        var appBicep = File.ReadAllText(appBicepPath);
        // The Radius Bicep extension must be declared first, e.g. `extension radius`.
        Assert.StartsWith("extension radius", appBicep.TrimStart());
        // The environment publisher ran.
        Assert.Contains("Radius.Core/environments", appBicep);
        // The container translation ran — without this the test would still pass if
        // AddContainer were silently omitted.
        Assert.Contains("Radius.Compute/containers", appBicep);
        Assert.Contains("nginx", appBicep);

        var bicepConfigPath = Path.Combine(outputDir, "bicepconfig.json");
        Assert.True(File.Exists(bicepConfigPath), $"Expected generated bicepconfig.json at '{bicepConfigPath}'.");
        // Parse the config and confirm it pins the Radius Bicep extension, e.g.:
        //   { "extensions": { "radius": "br:biceptypes.azurecr.io/radius:0.59" } }
        // Parsing (rather than a raw substring check) guards against malformed JSON
        // or a missing version tag. The exact version is intentionally not pinned
        // here to avoid churn when the Radius extension version bumps.
        using var bicepConfig = JsonDocument.Parse(File.ReadAllText(bicepConfigPath));
        var radiusExtension = bicepConfig.RootElement
            .GetProperty("extensions")
            .GetProperty("radius")
            .GetString();
        const string radiusExtensionPrefix = "br:biceptypes.azurecr.io/radius:";
        Assert.StartsWith(radiusExtensionPrefix, radiusExtension);
        // Ensure a version tag actually follows the prefix (e.g. `...:0.59`); a bare
        // prefix with no version would otherwise satisfy StartsWith. The exact
        // version is intentionally not pinned to avoid churn when the pin bumps.
        Assert.NotEmpty(radiusExtension![radiusExtensionPrefix.Length..]);
    }
}
