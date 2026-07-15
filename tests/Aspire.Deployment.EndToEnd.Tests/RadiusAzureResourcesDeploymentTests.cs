// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Gap-documenting tests for cloud-managed Azure resource references when publishing to Radius.
/// </summary>
/// <remarks>
/// Radius 0.59 supports portable recipe-backed resources such as <c>AddRedis</c>, but the
/// Aspire Radius publisher does not currently translate cloud-managed Azure resources such as
/// Key Vault, Storage, Service Bus, or Azure Managed Redis into Radius resources.
/// </remarks>
public sealed class RadiusAzureResourcesDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublishWithAzureKeyVaultReferenceDocumentsCurrentRadiusGap()
    {
        if (!DeploymentE2ETestHelpers.IsRunningInCI)
        {
            var localCurrentBuildStrategy = DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy();
            if (localCurrentBuildStrategy.Mode is CliInstallMode.InstallScript &&
                localCurrentBuildStrategy.Quality is null &&
                localCurrentBuildStrategy.Version is null)
            {
                Assert.Skip("Local Radius/Azure gap testing requires an explicit current-build CLI strategy. Set ASPIRE_E2E_ARCHIVE to a localhive archive or configure ASPIRE_E2E_QUALITY, ASPIRE_E2E_VERSION, or GITHUB_PR_NUMBER.");
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        using var workspace = TemporaryWorkspace.Create(output);
        const string projectName = "RadiusAzureGap";

        using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
        var pendingRun = terminal.RunAsync(cancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);
        await auto.InstallCurrentBuildAspireCliAsync(counter, output);

        output.WriteLine("Creating an empty AppHost for the Radius/Azure resource gap check...");
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

        await auto.TypeAsync($"cd {projectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Radius --all --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        await auto.TypeAsync("aspire add Aspire.Hosting.Azure.KeyVault --all --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        var appHostFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            projectName,
            $"{projectName}.AppHost",
            "AppHost.cs");

        File.WriteAllText(appHostFilePath,
            """
            // Licensed to the .NET Foundation under one or more agreements.
            // The .NET Foundation licenses this file to you under the MIT license.

            #pragma warning disable ASPIREPIPELINES001
            #pragma warning disable ASPIRERADIUS003

            var builder = DistributedApplication.CreateBuilder(args);

            var radius = builder.AddRadiusEnvironment("radius")
                .WithAzureProvider(
                    subscriptionId: "11111111-1111-1111-1111-111111111111",
                    resourceGroup: "rg-radius-gap",
                    azure => azure.WithWorkloadIdentity(
                        tenantId: "22222222-2222-2222-2222-222222222222",
                        clientId: "33333333-3333-3333-3333-333333333333"));

            var vault = builder.AddAzureKeyVault("vault");

            builder.AddContainer("consumer", "mcr.microsoft.com/dotnet/samples", "aspnetapp")
                .WithReference(vault)
                .WithComputeEnvironment(radius);

            builder.Build().Run();
            """);

        await auto.TypeAsync($"cd {projectName}.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("Publishing is expected to time out: Radius tries to resolve Azure Bicep outputs during container env emission.");
        await auto.TypeAsync(
            "set +e; " +
            "mkdir -p \"$HOME/.aspire/logs\"; " +
            "rm -rf ../out ../publish.log ../radius-gap-evidence.log ../radius-gap-log-start; " +
            ": > ../radius-gap-log-start; " +
            "timeout 45s aspire publish --output-path ../out --non-interactive > ../publish.log 2>&1; " +
            "code=$?; " +
            "if [ \"$code\" = \"124\" ]; then " +
            "if [ -f ../out/app.bicep ]; then cat ../publish.log; echo \"Radius app.bicep should not be produced for the current gap.\"; exit 1; fi; " +
            "logs=$(find \"$HOME/.aspire/logs\" -maxdepth 1 -name 'cli_*.log' -newer ../radius-gap-log-start -print); " +
            "if [ -z \"$logs\" ]; then cat ../publish.log; echo \"Radius publish timed out, but no invocation-specific CLI log was produced.\"; exit 1; fi; " +
            "if ! grep -hE \"Azure[.]BicepOutputReference[.]GetValueAsync|RadiusInfrastructureBuilder[.]ResolveEnvironmentAsync\" $logs > ../radius-gap-evidence.log 2>/dev/null; then " +
            "cat ../publish.log; " +
            "echo \"Radius publish timed out, but this invocation's CLI logs did not show Azure Bicep output resolution evidence.\"; " +
            "printf '%s\\n' $logs; " +
            "exit 1; " +
            "fi; " +
            "tail -8 ../radius-gap-evidence.log; " +
            // Split the marker token in the source command (RADIUS_AZURE_REFERENCE_GAP''_TIMEOUT
            // evaluates to RADIUS_AZURE_REFERENCE_GAP_TIMEOUT) so Hex1b cannot self-match the
            // echoed command line before the shell actually reaches the gap assertion.
            "echo RADIUS_AZURE_REFERENCE_GAP''_TIMEOUT; " +
            "exit 0; " +
            "fi; " +
            "cat ../publish.log; " +
            "echo \"Expected Radius publish to time out while resolving Azure resource references, but exit code was $code.\"; " +
            "exit 1");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("RADIUS_AZURE_REFERENCE_GAP_TIMEOUT").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(90),
            description: "Radius/Azure reference gap timeout marker");
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
