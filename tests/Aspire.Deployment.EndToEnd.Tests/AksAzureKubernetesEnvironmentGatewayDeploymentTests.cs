// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the <c>AddAzureKubernetesEnvironment</c> + <c>AddLoadBalancer</c> +
/// <c>AddGateway</c> story. The Aspire deploy pipeline provisions the AKS cluster, ACR, VNet,
/// the AGC ingress profile (via the Bicep change in this PR), the AGC ALB controller add-on,
/// the <c>ApplicationLoadBalancer</c> CR, and the Gateway API <c>Gateway</c> + <c>HTTPRoute</c>
/// resources. The test then waits for the AGC data plane to assign an FQDN to the gateway and
/// verifies the API service is reachable over plain HTTP via that FQDN.
/// TLS issuance via cert-manager is intentionally NOT exercised here; that is covered by
/// <see cref="KubernetesGatewayTlsDeploymentTests"/>.
/// </summary>
public sealed class AksAzureKubernetesEnvironmentGatewayDeploymentTests(ITestOutputHelper output)
{
    // Provisioning AKS + AGC takes ~15 min, deploy + verify a few more — give 60 min headroom.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(60);

    [Fact]
    public async Task DeployApiWithGatewayToAzureKubernetesEnvironment()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployApiWithGatewayToAzureKubernetesEnvironmentCore(cancellationToken);
    }

    private async Task DeployApiWithGatewayToAzureKubernetesEnvironmentCore(CancellationToken cancellationToken)
    {
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }
            else
            {
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("aksgw");
        var projectName = "AksGatewayApi";

        output.WriteLine($"Test: {nameof(DeployApiWithGatewayToAzureKubernetesEnvironment)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // Step 1: Prepare environment
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 2: Set up CLI environment
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create starter project (no Redis — we just need an API service to expose).
            output.WriteLine("Step 3: Creating Aspire starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Azure.Kubernetes package
            output.WriteLine("Step 5: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Patch AppHost.cs with AddAzureKubernetesEnvironment + AddLoadBalancer + AddGateway.
            //
            // The patched snippet mirrors playground/AksDemo/AksDemo.AppHost/AppHost.cs but
            // intentionally drops cert-manager / WithTls / cluster-issuer wiring — we want the
            // raw Bicep-provisions-AGC + gateway-attaches-to-LB code paths covered without
            // dragging Let's Encrypt into this test.
            //
            // We rely on the starter template exposing `var apiService = builder.AddProject<Projects.<name>_ApiService>("apiservice")`.
            // The webfrontend line is left untouched; we don't route to it from the gateway.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 6: Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            // The AGC ingress profile + ApplicationLoadBalancer + Gateway/HTTPRoute pieces that
            // this PR adds. Inject before builder.Build().Run();. Use Standard_D2as_v5 to match
            // the other AKS deployment tests' SKU/region quota story.
            const string buildRunPattern = "builder.Build().Run();";
            const string replacement = """
// VNet layout chosen to avoid the AKS default service CIDR (10.0.0.0/16):
//   10.100.0.0/16   - vnet
//     10.100.0.0/22 - aks node pool subnet
//     10.100.4.0/24 - AGC frontend subnet (delegated to ServiceNetworking by AddLoadBalancer)
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.100.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-nodes", "10.100.0.0/22");
var albSubnet = vnet.AddSubnet("alb-public", "10.100.4.0/24");

var aks = builder.AddAzureKubernetesEnvironment("aks")
    .WithSubnet(aksSubnet)
    .WithSystemNodePool("Standard_D2as_v5");
aks.AddNodePool("workload", "Standard_D2as_v5", minCount: 1, maxCount: 3);

// AddLoadBalancer creates the AGC ApplicationLoadBalancer CR, delegates the frontend subnet
// to Microsoft.ServiceNetworking, and (per this PR) ensures the AGC managed identity gets
// Network Contributor on the subnet so the controller can program the data plane.
var publicLb = aks.AddLoadBalancer("public", albSubnet);

// Gateway with a single route that points at / on the apiService. WithLoadBalancer
// stamps the alb.networking.azure.io association annotations and defaults the
// gatewayClassName to "azure-alb-external". Routing "/" (Prefix) so any path the
// starter template's apiservice exposes (/, /weatherforecast) flows through.
aks.AddGateway("api-gw")
    .WithLoadBalancer(publicLb)
    .WithRoute("/", apiService.GetEndpoint("http"));

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            // Fail loudly if the starter template ever drops the literal we patch on:
            // without this guard, `Replace` silently returns the original string and the
            // test would deploy a stock starter app and report "success" without
            // exercising any of the AGC / Gateway / LoadBalancer code paths under test.
            Assert.Contains(buildRunPattern, content);

            // Required pragmas for the new (still experimental) AGC + pipeline surface.
            const string pragmaBlock =
                "#pragma warning disable ASPIREPIPELINES001\n" +
                "#pragma warning disable ASPIRECOMPUTE003\n" +
                "#pragma warning disable ASPIREAZURE003\n";

            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = pragmaBlock + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with AddAzureKubernetesEnvironment + AddLoadBalancer + AddGateway");

            // Step 7: Navigate to AppHost project directory
            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 8: Set environment variables for deployment.
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts.
            // - Set Azure location to westus3 (where we have Standard_D2as_v5 capacity, matching
            //   the rest of the AKS deployment tests).
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name so the finally
            //   block can clean it up.
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Deploy.
            // --clear-cache prevents reuse of any cached location/RG from a previous local run.
            output.WriteLine("Step 9: Starting AKS deployment (provisioning AKS + AGC takes 10-15 min)...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 10: Get AKS credentials for the auto-provisioned cluster.
            output.WriteLine("Step 10: Getting AKS credentials...");
            await auto.TypeAsync($"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                  $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Step 11: Wait for all pods to be ready across namespaces (the helm release that
            // aspire deploy installs lands in its own namespace named after the project).
            output.WriteLine("Step 11: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all --all-namespaces --timeout=300s 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync("kubectl get pods --all-namespaces && kubectl get gateway --all-namespaces");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 12: Discover the namespace where the api-gw gateway lives (set by helm release
            // namespace, which aspire deploy chooses based on the project name).
            output.WriteLine("Step 12: Discovering gateway namespace...");
            await auto.TypeAsync("NS=$(kubectl get gateway --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"api-gw\")]}{.metadata.namespace}{end}') && echo \"Namespace: $NS\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 13: Wait for AGC to assign an FQDN to the gateway. AGC data plane provisioning
            // can take 5-10 min on a fresh cluster the first time the ALB association lands.
            output.WriteLine("Step 13: Waiting for AGC to assign gateway FQDN (up to 15 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 90); do " +
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}' 2>/dev/null); " +
                "[ -n \"$FQDN\" ] && echo \"Gateway FQDN: $FQDN\" && OK=1 && break; " +
                "echo \"Attempt $i: waiting for AGC FQDN...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: gateway never received AGC FQDN'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(16));

            // Step 14: Verify the API responds over the AGC FQDN. Because AGC programs the data
            // plane asynchronously after the FQDN is published, retry for a couple of minutes.
            // /weatherforecast is the actual API endpoint exposed by the starter template
            // apiservice — the gateway is wired to "/" (Prefix) so the path flows through to it.
            output.WriteLine("Step 14: Verifying http://<fqdn>/weatherforecast returns 200...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "echo \"Testing: http://$FQDN/weatherforecast\" && " +
                "OK=0; for i in $(seq 1 30); do sleep 5; " +
                "S=$(curl -so /dev/null -w '%{http_code}' -m 10 http://$FQDN/weatherforecast 2>/dev/null); " +
                "[ \"$S\" = \"200\" ] && echo \"HTTP $S OK\" && OK=1 && break; " +
                "echo \"Attempt $i: HTTP $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: gateway never returned 200 via AGC FQDN'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // Step 15: Sanity-check via port-forward. This isolates "app is healthy" from
            // "AGC routing is healthy" and matches the pattern used by AksBlazorRedis.
            output.WriteLine("Step 15: Verifying apiservice via port-forward...");
            await auto.TypeAsync("kubectl port-forward svc/apiservice-service 18080:8080 -n $NS > /dev/null 2>&1 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync("sleep 3");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // /weatherforecast is the actual API endpoint exposed by the starter template
            // apiservice in non-Development. Fail explicitly if all retries are exhausted.
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 10); do sleep 3 && " +
                "curl -sf http://localhost:18080/weatherforecast -o /dev/null -w '%{http_code}' && " +
                "echo ' OK' && OK=1 && break; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: apiservice unreachable via port-forward'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // Step 16: Tear down via the Aspire pipeline.
            output.WriteLine("Step 16: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployApiWithGatewayToAzureKubernetesEnvironment),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployApiWithGatewayToAzureKubernetesEnvironment),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Fire-and-forget RG delete; the hourly cleanup workflow handles any misses.
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName);
        }
    }

    private void TriggerCleanupResourceGroup(string resourceGroupName)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            output.WriteLine($"Cleanup triggered for resource group: {resourceGroupName}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to trigger cleanup: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
        }
    }
}
