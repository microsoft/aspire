// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for the Kubernetes ingress/gateway validation that
/// requires routed endpoints to be marked external (see
/// <c>EndpointRoutingValidation.ThrowIfEndpointNotExternal</c>). The CLI
/// surface check matters because the validation throws during model
/// materialization on the <c>aspire publish</c> path and we want a regression
/// guard that exercises the full publish pipeline, not just the unit-level
/// helper.
/// </summary>
public sealed class KubernetesPublishRequiresExternalEndpointTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireK8sExternalCheck";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task IngressWithoutExternalEndpoint_FailsPublishWithGuidance()
    {
        await RunPublishFailureScenarioAsync(
            // Wire an ingress that routes a non-external HTTP endpoint. The
            // publish-time validation in EndpointRoutingValidation should
            // throw before any Helm output is generated.
            appHostBodyExtension: """
            var kube = builder.AddKubernetesEnvironment("kube");
            var api = builder.AddContainer("api", "nginx").WithHttpEndpoint(targetPort: 80);
            kube.AddIngress("public").WithRoute("/", api.GetEndpoint("http"));
            """);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task GatewayWithoutExternalEndpoint_FailsPublishWithGuidance()
    {
        await RunPublishFailureScenarioAsync(
            appHostBodyExtension: """
            var kube = builder.AddKubernetesEnvironment("kube");
            var api = builder.AddContainer("api", "nginx").WithHttpEndpoint(targetPort: 80);
            kube.AddGateway("public").WithRoute("/", api.GetEndpoint("http"));
            """);
    }

    private async Task RunPublishFailureScenarioAsync(string appHostBodyExtension)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);

            // The starter template gives us the conventional
            // `{ProjectName}/{ProjectName}.AppHost/AppHost.cs` layout, matching
            // KubernetesPublishTests so the AppHost-mutation logic below stays
            // consistent across both tests.
            await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

            // cd into the project so subsequent `aspire add` and `aspire publish`
            // commands resolve the AppHost via repo-root discovery.
            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The Kubernetes hosting package is required to compile the AppHost code
            // we're about to write. `aspire add` resolves the version against the
            // same feed configuration the rest of the CLI uses (including PR builds).
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            // Patch AppHost.cs in-place. The Starter template's AppHost.cs ends
            // with `builder.Build().Run();`; we insert the K8s wiring immediately
            // before it. Failing to find the marker should surface as a clear
            // test failure rather than a silently no-op publish.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");
            var content = File.ReadAllText(appHostFilePath);
            const string buildRunPattern = "builder.Build().Run();";
            Assert.Contains(buildRunPattern, content);
            content = content.Replace(buildRunPattern, appHostBodyExtension + Environment.NewLine + buildRunPattern);
            File.WriteAllText(appHostFilePath, content);

            // ASPIRE_PLAYGROUND interferes with `--non-interactive`. See
            // KubernetesPublishTests for full context.
            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Drive aspire publish. The validation throws an InvalidOperationException
            // during model materialization, so publish should exit with a non-zero code
            // and surface our guidance message verbatim in stderr/stdout.
            await auto.TypeAsync("aspire publish -o helm-output --non-interactive");
            await auto.EnterAsync();

            var expectedCounter = counter.Value;
            // We don't pin to a specific exit code — the publish pipeline currently
            // surfaces validation failures as exit 1, but treating any non-zero
            // ERR:* prompt as the success condition keeps this test stable across
            // future exit-code refactors.
            var errorPromptSearcher = new CellPatternSearcher()
                .FindPattern(expectedCounter.ToString(CultureInfo.InvariantCulture))
                .RightText(" ERR:");

            await auto.WaitUntilAsync(
                snapshot => errorPromptSearcher.Search(snapshot).Count > 0,
                TimeSpan.FromMinutes(5),
                description: "waiting for aspire publish to fail");
            counter.Increment();

            // After the publish exits, scrape the screen for the guidance fragments.
            // We use a generous WaitUntilTextAsync so any in-progress rendering
            // settles before we assert.
            await auto.WaitUntilTextAsync("WithExternalHttpEndpoints", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("'api'", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("'public'", timeout: TimeSpan.FromSeconds(30));
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }
}
