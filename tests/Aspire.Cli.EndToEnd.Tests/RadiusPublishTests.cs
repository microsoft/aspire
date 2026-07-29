// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
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
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishTypeScriptAppHostWithRadiusEnvironment_EmitsCustomizedInfrastructure()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Radius.", "Aspire.Hosting.Redis."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        const string appHost = """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            const radius = await builder.addRadiusEnvironment('radius');
            await radius.withNamespace('radius-system');
            await radius.withAzureProvider(
                '00000000-0000-0000-0000-000000000000',
                'radius-validation',
                async (azure) => {
                    await azure.withWorkloadIdentity(
                        '11111111-1111-1111-1111-111111111111',
                        '22222222-2222-2222-2222-222222222222');
                });
            await radius.withAwsProvider(
                '123456789012',
                'us-west-2',
                async (aws) => {
                    await aws.withIrsa('arn:aws:iam::123456789012:role/radius-validation');
                });

            await radius.configureRadiusInfrastructure(async (options) => {
                const environment = await (await options.environments()).get(0);
                await environment
                    .withEnvironmentName('custom-radius-environment')
                    .withKubernetesNamespace('custom-radius-namespace');

                const application = await (await options.applications()).get(0);
                await application.withApplicationName('custom-radius-application');

                const recipePack = await (await options.recipePacks()).get(0);
                await recipePack
                    .withRecipePackName('custom-radius-recipes')
                    .withRecipe(
                        'Custom.Resources/widgets',
                        'bicep',
                        'ghcr.io/example/radius-recipes/widget:1.0');

                const customResource = await options.addResourceTypeInstance(
                    'custom_widget',
                    'Custom.Resources/widgets',
                    '2025-01-01-preview');
                await customResource
                    .withResourceName('custom-widget')
                    .withResourceRecipeName('default')
                    .withResourceScope('app', 'radius')
                    .withStringRecipeParameter('sku', 'small');

                const container = await (await options.containers()).get(0);
                await container
                    .withImage('nginx:alpine')
                    .withContainerEnvironmentVariable('RADIUS_TEST', 'true')
                    .withContainerPort('http', 8080, 'TCP')
                    .withContainerConnection('widget', 'custom_widget');

                const legacyEnvironment = await (await options.legacyEnvironments()).get(0);
                await legacyEnvironment.withLegacyEnvironment(
                    'custom-legacy-environment',
                    'custom-radius-namespace');

                const legacyApplication = await (await options.legacyApplications()).get(0);
                await legacyApplication.withLegacyApplicationName('custom-legacy-application');
            });

            await builder.addContainer('web', 'nginx');
            await builder.addRedis('cache');
            await builder.build().run();
            """;
        File.WriteAllText(appHostFilePath, appHost);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish -o radius-output --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        var outputDir = Path.Combine(workspace.WorkspaceRoot.FullName, "radius-output");

        var appBicepPath = Path.Combine(outputDir, "app.bicep");
        Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
        var appBicep = File.ReadAllText(appBicepPath);
        Assert.StartsWith("extension radius", appBicep.TrimStart());
        Assert.Contains("Radius.Core/environments", appBicep);
        Assert.Contains("Radius.Compute/containers", appBicep);
        Assert.Contains("Custom.Resources/widgets@2025-01-01-preview", appBicep);
        Assert.Contains("custom-radius-environment", appBicep);
        Assert.Contains("custom-radius-application", appBicep);
        Assert.Contains("custom-radius-recipes", appBicep);
        Assert.Contains("custom-legacy-environment", appBicep);
        Assert.Contains("custom-legacy-application", appBicep);
        Assert.Contains("custom-widget", appBicep);
        Assert.Contains("RADIUS_TEST", appBicep);
        Assert.Contains("custom_widget.id", appBicep);

        var bicepConfigPath = Path.Combine(outputDir, "bicepconfig.json");
        Assert.True(File.Exists(bicepConfigPath), $"Expected generated bicepconfig.json at '{bicepConfigPath}'.");
        using var bicepConfig = JsonDocument.Parse(File.ReadAllText(bicepConfigPath));
        var radiusExtension = bicepConfig.RootElement
            .GetProperty("extensions")
            .GetProperty("radius")
            .GetString();
        const string radiusExtensionPrefix = "br:biceptypes.azurecr.io/radius:";
        Assert.StartsWith(radiusExtensionPrefix, radiusExtension);
        Assert.NotEmpty(radiusExtension![radiusExtensionPrefix.Length..]);
    }
}
