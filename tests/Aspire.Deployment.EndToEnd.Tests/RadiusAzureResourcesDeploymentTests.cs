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
/// Key Vault, Storage, Service Bus, or Azure Managed Redis into Radius resources. Today
/// <c>aspire publish</c> hangs in that scenario; the tracking issue
/// (https://github.com/microsoft/aspire/issues/18802) covers making it fail fast instead. The
/// test is marked <c>[ActiveIssue]</c> and asserts that intended fail-fast behavior, so it starts
/// passing once the gap is closed.
/// </remarks>
public sealed class RadiusAzureResourcesDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/18802")]
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

            builder.AddContainer("consumer", "mcr.microsoft.com/azuredocs/aci-helloworld", "latest")
                .WithImageSHA256("456a1150aa41340a14c7be1342deda2cde9e6e7df9fde6b8a69de0ae04f92fad")
                .WithReference(vault)
                .WithComputeEnvironment(radius);

            builder.Build().Run();
            """);

        await auto.TypeAsync($"cd {projectName}.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        output.WriteLine("Publishing should fail fast once the gap is fixed: Aspire cannot translate a cloud-managed Azure resource (Key Vault) into a Radius resource. See https://github.com/microsoft/aspire/issues/18802.");
        await auto.TypeAsync(
            "set +e; " +
            "rm -rf ../out ../publish.log; " +
            // The bug in #18802 causes `aspire publish` to HANG (Radius resolves Azure Bicep outputs
            // during container env emission). Cap it with `timeout` so an unfixed build fails the
            // test loudly instead of hanging. Once #18802 is fixed, publish should fail fast with a
            // non-zero exit and produce no app.bicep.
            "timeout 120s aspire publish --output-path ../out --non-interactive > ../publish.log 2>&1; " +
            "code=$?; " +
            "if [ \"$code\" = \"124\" ]; then cat ../publish.log; echo \"Radius publish still hangs on a cloud-managed Azure reference (see issue 18802).\"; exit 1; fi; " +
            "if [ \"$code\" = \"0\" ]; then cat ../publish.log; echo \"Radius publish unexpectedly succeeded for a cloud-managed Azure reference; it should fail fast until Azure resource translation is supported.\"; exit 1; fi; " +
            "if [ -f ../out/app.bicep ]; then cat ../publish.log; echo \"Radius publish produced app.bicep for a cloud-managed Azure reference; it should fail before emitting output.\"; exit 1; fi; " +
            // Split the marker token in the source command (RADIUS_AZURE_REFERENCE_GAP''_FAILFAST
            // evaluates to RADIUS_AZURE_REFERENCE_GAP_FAILFAST) so Hex1b cannot self-match the
            // echoed command line before the shell actually reaches the gap assertion.
            "echo RADIUS_AZURE_REFERENCE_GAP''_FAILFAST; " +
            "exit 0");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("RADIUS_AZURE_REFERENCE_GAP_FAILFAST").Search(s).Count > 0,
            timeout: TimeSpan.FromMinutes(3),
            description: "Radius/Azure reference gap fail-fast marker");
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
