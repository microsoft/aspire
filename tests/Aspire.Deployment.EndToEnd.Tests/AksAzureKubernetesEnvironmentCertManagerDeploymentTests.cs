// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the typed cert-manager API on top of <c>AddAzureKubernetesEnvironment</c>:
/// <c>aks.AddCertManager("cert-manager")</c> + <c>AddIssuer().WithLetsEncryptStaging(email).WithHttp01Solver()</c>
/// combined with <c>gateway.WithTls(issuer)</c>. The Aspire deploy pipeline provisions the AKS
/// cluster, ACR, VNet, AGC, installs the cert-manager Helm chart, applies the <c>ClusterIssuer</c>
/// manifest, pre-creates the bootstrap TLS secret, and patches the discovered AGC FQDN onto
/// the gateway's HTTPS listener. The test then waits for cert-manager to complete the HTTP-01
/// challenge against the AGC FQDN and replace the placeholder cert with a real one, and
/// verifies the gateway is serving a Let's Encrypt-issued certificate.
///
/// Uses Let's Encrypt <em>staging</em> (not production) because production has strict per-domain
/// rate limits (~5 certs / week) that nightly + on-demand E2E runs would burn through quickly.
/// Staging issues real ACME certs from a CA that identifies itself as "(STAGING) Let's Encrypt",
/// which still proves the full HTTP-01 + Gateway API solver flow end-to-end.
/// </summary>
public sealed class AksAzureKubernetesEnvironmentCertManagerDeploymentTests(ITestOutputHelper output)
{
    // Provisioning AKS + AGC + cert-manager takes ~15 min, deploy + cert issuance another
    // ~10 min in the worst case (ACME order can take a few minutes once the listener is patched).
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(75);

    [Fact]
    public async Task DeployApiWithCertManagerToAzureKubernetesEnvironment()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployApiWithCertManagerToAzureKubernetesEnvironmentCore(cancellationToken);
    }

    private async Task DeployApiWithCertManagerToAzureKubernetesEnvironmentCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akscm");
        var projectName = "AksCertManager";

        // ACME registration email. Staging accepts any well-formed address, so we use a
        // dedicated one that's clearly identifiable as automation.
        const string acmeEmail = "aspire-e2e-test@microsoft.com";

        output.WriteLine($"Test: {nameof(DeployApiWithCertManagerToAzureKubernetesEnvironment)}");
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

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            output.WriteLine("Step 3: Creating Aspire starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 5: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Patch AppHost.cs with AddAzureKubernetesEnvironment + AddLoadBalancer + AddCertManager
            // + AddIssuer + WithTls(issuer). This is the cert-manager-enabled variant of
            // AksAzureKubernetesEnvironmentGatewayDeploymentTests' AppHost — the ONLY differences
            // from that test's AppHost are the AddCertManager/AddIssuer calls and replacing the
            // gateway's plain "/" route with WithTls(letsEncrypt).
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 6: Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

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

var publicLb = aks.AddLoadBalancer("public", albSubnet);

// Email registered with the staging ACME endpoint. Passed as a parameter so the test
// supplies it via Parameters__acmeemail without burning it into AppHost source.
var acmeEmail = builder.AddParameter("acmeemail");

// Install cert-manager via the typed API and declare a Let's Encrypt STAGING ClusterIssuer.
// Staging is intentional — production rate limits would block repeat E2E runs. The
// staging endpoint exercises the same HTTP-01 + Gateway API solver flow end-to-end.
var certManager = aks.AddCertManager("cert-manager");
var letsEncrypt = certManager.AddIssuer("letsencrypt-staging")
    .WithLetsEncryptStaging(acmeEmail)
    .WithHttp01Solver();

// Gateway with HTTPS listener. WithTls(letsEncrypt) creates the listener AND adds the
// cert-manager.io/cluster-issuer annotation in one call. Once AGC assigns the gateway
// its FQDN, the tls-fqdn-discovery pipeline step patches it onto the listener and the
// cm-issuer-apply step ensures the ClusterIssuer is present so cert-manager can complete
// the HTTP-01 challenge against the AGC FQDN.
// The Gateway route validation requires the routed endpoint to be marked external.
apiService.WithExternalHttpEndpoints();

aks.AddGateway("api-gw")
    .WithLoadBalancer(publicLb)
    .WithRoute("/", apiService.GetEndpoint("http"))
    .WithTls(letsEncrypt);

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            const string pragmaBlock =
                "#pragma warning disable ASPIREPIPELINES001\n" +
                "#pragma warning disable ASPIRECOMPUTE003\n" +
                "#pragma warning disable ASPIREAZURE003\n";

            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = pragmaBlock + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with AddCertManager + AddIssuer + WithTls(issuer)");

            output.WriteLine("Step 7: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 8: Setting deployment environment variables...");
            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && " +
                $"export AZURE__LOCATION=westus3 && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export Parameters__acmeemail={acmeEmail}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The deploy pipeline provisions AKS + AGC, installs the cert-manager helm chart,
            // applies the ClusterIssuer, and pre-creates the bootstrap TLS secret. Use a
            // generous timeout to cover the AKS+AGC bring-up window.
            output.WriteLine("Step 9: Starting AKS + cert-manager deployment (15-20 min)...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(40));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 10: Getting AKS credentials...");
            await auto.TypeAsync(
                $"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 11: Waiting for pods to be ready...");
            await auto.TypeAsync("kubectl wait --for=condition=ready pod --all --all-namespaces --timeout=300s 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync(
                "kubectl get pods --all-namespaces && " +
                "kubectl get gateway --all-namespaces && " +
                "kubectl get clusterissuer && " +
                "kubectl get certificate --all-namespaces");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Verify the ClusterIssuer the cm-issuer-apply pipeline step was supposed to create
            // is present and Ready. Without this, cert-manager's CertificateRequest would sit
            // in 'IssuerNotFound' indefinitely.
            output.WriteLine("Step 12: Verifying ClusterIssuer is Ready...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 30); do " +
                "READY=$(kubectl get clusterissuer letsencrypt-staging -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'ClusterIssuer Ready' && OK=1 && break; " +
                "echo \"Attempt $i: ClusterIssuer status=$READY, waiting...\"; sleep 5; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: letsencrypt-staging ClusterIssuer never became Ready'; kubectl describe clusterissuer letsencrypt-staging; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 13: Discovering gateway namespace...");
            await auto.TypeAsync(
                "NS=$(kubectl get gateway --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"api-gw\")]}{.metadata.namespace}{end}') && " +
                "echo \"Namespace: $NS\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Wait for AGC to assign the gateway an FQDN. The hosting integration pre-creates
            // a self-signed bootstrap TLS secret so AGC will program the gateway listener even
            // before cert-manager has issued the real certificate; without it the listener
            // would deadlock waiting for a secret that doesn't exist yet.
            output.WriteLine("Step 14: Waiting for AGC to assign gateway FQDN (up to 15 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 90); do " +
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}' 2>/dev/null); " +
                "[ -n \"$FQDN\" ] && echo \"Gateway FQDN: $FQDN\" && OK=1 && break; " +
                "echo \"Attempt $i: waiting for AGC FQDN...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: gateway never received AGC FQDN'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(16));

            // Wait for cert-manager to complete the HTTP-01 challenge and replace the
            // bootstrap placeholder cert with a real (staging) Let's Encrypt cert.
            output.WriteLine("Step 15: Waiting for cert-manager to issue the certificate (up to 10 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 60); do " +
                "READY=$(kubectl get certificate -n $NS api-gw-tls -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'Certificate Ready' && OK=1 && break; " +
                "echo \"Attempt $i: certificate Ready=$READY, waiting...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: certificate never became Ready'; " +
                "kubectl describe certificate -n $NS api-gw-tls; " +
                "kubectl get challenge,order -n $NS; " +
                "exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(11));

            // Probe the served cert and assert the issuer string contains "Let's Encrypt".
            // The CA chain on a staging cert is "(STAGING) Let's Encrypt"; on production it's
            // plain "Let's Encrypt" — both match a case-insensitive grep on "let's encrypt".
            //
            // Using openssl s_client (with -servername for SNI) rather than curl because curl
            // would refuse the staging cert at TLS layer; we want the cert details regardless
            // of trust. AGC also takes a few seconds to load the new cert into the data plane
            // after the secret is updated, so retry the probe.
            output.WriteLine("Step 16: Verifying served cert is from Let's Encrypt...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "echo \"Probing https://$FQDN\" && " +
                "OK=0; for i in $(seq 1 24); do sleep 5; " +
                "ISSUER=$(echo | openssl s_client -connect $FQDN:443 -servername $FQDN 2>/dev/null | " +
                "openssl x509 -noout -issuer 2>/dev/null); " +
                "echo \"Attempt $i: issuer=$ISSUER\"; " +
                "echo \"$ISSUER\" | grep -i \"let's encrypt\" >/dev/null && " +
                "echo \"PASS: Let's Encrypt cert observed: $ISSUER\" && OK=1 && break; " +
                "done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: served cert is not from Let'\\''s Encrypt'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Verify HTTPS actually serves the apiservice. Using -k (insecure) because the
            // staging Let's Encrypt CA isn't in the system trust store; the previous step
            // already proved cryptographic identity (issuer == Let's Encrypt).
            output.WriteLine("Step 17: Verifying https://<fqdn>/weatherforecast returns 200...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "OK=0; for i in $(seq 1 30); do sleep 5; " +
                "S=$(curl -kso /dev/null -w '%{http_code}' -m 10 https://$FQDN/weatherforecast 2>/dev/null); " +
                "[ \"$S\" = \"200\" ] && echo \"HTTPS $S OK\" && OK=1 && break; " +
                "echo \"Attempt $i: HTTPS $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: HTTPS endpoint never returned 200'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // Re-deploy without --clear-cache to exercise the helm UPGRADE path on top of the
            // already-installed cert-manager release. This guards against bugs that only manifest
            // on the second deploy — e.g. helm CLI flag parsing changes between major versions
            // (helm v4 made --server-side a string flag, which would silently consume the
            // following --force-conflicts as its value during install and then fail every
            // subsequent upgrade with "invalid/unknown release server-side apply method:
            // --force-conflicts"). The first deploy alone would not catch this.
            output.WriteLine("Step 18: Re-deploying to validate helm upgrade idempotency...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(20));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 19: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployApiWithCertManagerToAzureKubernetesEnvironment),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed - cert-manager issued a Let's Encrypt cert via HTTP-01!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployApiWithCertManagerToAzureKubernetesEnvironment),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
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
