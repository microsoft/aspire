// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the one-line <c>aks.WithSimplifiedDeployment(acmeEmail)</c> recipe
/// (#17160). Mirrors <see cref="AksAzureKubernetesEnvironmentCertManagerDeploymentTests"/>
/// but exercises the collapsed surface: no VNet/subnet/load-balancer/cert-manager/issuer/gateway
/// calls in the AppHost — just <c>WithSimplifiedDeployment</c> + a project with
/// <c>WithExternalHttpEndpoints</c>, and the auto-router takes care of routing.
///
/// Also asserts the Phase 1 (#17158) TLS posture: a plain HTTP request to <c>/api</c>
/// returns a 301 redirect to HTTPS, and HTTPS responses carry the HSTS header.
///
/// Uses Let's Encrypt <em>staging</em> because production has strict per-domain rate
/// limits (~5 certs / week) that would block repeat E2E runs.
/// </summary>
public sealed class AksSimplifiedDeploymentTests(ITestOutputHelper output)
{
    // AKS + AGC bring-up + cert issuance, plus the second-deploy idempotency probe.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(75);

    [Fact]
    public async Task DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironment()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironmentCore(cancellationToken);
    }

    private async Task DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironmentCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akscd");
        var projectName = "AksSimplifiedDeployment";

        // ACME registration email. Staging accepts any well-formed address.
        const string acmeEmail = "aspire-e2e-test@microsoft.com";

        output.WriteLine($"Test: {nameof(DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironment)}");
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

            // Patch AppHost.cs to use the one-line WithSimplifiedDeployment recipe. Switch the
            // auto-provisioned issuer to Let's Encrypt staging via the options callback so we
            // don't burn the production rate limit on repeat E2E runs.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 6: Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            const string buildRunPattern = "builder.Build().Run();";
            const string replacement = """
// ACME registration email. Passed as a parameter so the test supplies it via
// Parameters__acmeemail without burning it into AppHost source.
var acmeEmail = builder.AddParameter("acmeemail");

// The whole VNet + AKS + AGC + cert-manager + Gateway + TLS recipe collapses to this:
builder.AddAzureKubernetesEnvironment("aks")
    .WithSimplifiedDeployment(acmeEmail, o =>
    {
        // Staging only — production has tight rate limits that would block repeat E2E runs.
        o.AcmeEnvironment = Aspire.Hosting.Azure.Kubernetes.LetsEncryptEnvironment.Staging;
    });

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            // Also flip the starter's apiService.WithExternalHttpEndpoints — it's there by
            // default in `aspire new`'s starter template, which is exactly what
            // WithSimplifiedDeployment's auto-router picks up. No further AppHost.cs edits required.

            const string pragmaBlock =
                "#pragma warning disable ASPIREPIPELINES001\n" +
                "#pragma warning disable ASPIRECOMPUTE003\n" +
                "#pragma warning disable ASPIREAZURE003\n";

            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = pragmaBlock + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with WithSimplifiedDeployment(acmeEmail)");

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

            output.WriteLine("Step 9: Starting AKS + WithSimplifiedDeployment deployment (15-20 min)...");
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

            // Verify the auto-provisioned ClusterIssuer (default name "letsencrypt") is Ready.
            output.WriteLine("Step 12: Verifying ClusterIssuer is Ready...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 30); do " +
                "READY=$(kubectl get clusterissuer letsencrypt -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'ClusterIssuer Ready' && OK=1 && break; " +
                "echo \"Attempt $i: ClusterIssuer status=$READY, waiting...\"; sleep 5; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: letsencrypt ClusterIssuer never became Ready'; kubectl describe clusterissuer letsencrypt; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // The auto-provisioned gateway is "public-gw" (SimplifiedDeploymentOptions.GatewayName default).
            output.WriteLine("Step 13: Discovering gateway namespace...");
            await auto.TypeAsync(
                "NS=$(kubectl get gateway --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"public-gw\")]}{.metadata.namespace}{end}') && " +
                "echo \"Namespace: $NS\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 14: Waiting for AGC to assign gateway FQDN (up to 15 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 90); do " +
                "FQDN=$(kubectl get gateway public-gw -n $NS -o jsonpath='{.status.addresses[0].value}' 2>/dev/null); " +
                "[ -n \"$FQDN\" ] && echo \"Gateway FQDN: $FQDN\" && OK=1 && break; " +
                "echo \"Attempt $i: waiting for AGC FQDN...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: gateway never received AGC FQDN'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(16));

            output.WriteLine("Step 15: Waiting for cert-manager to issue the certificate (up to 10 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 60); do " +
                "READY=$(kubectl get certificate -n $NS public-gw-tls -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'Certificate Ready' && OK=1 && break; " +
                "echo \"Attempt $i: certificate Ready=$READY, waiting...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: certificate never became Ready'; " +
                "kubectl describe certificate -n $NS public-gw-tls; " +
                "kubectl get challenge,order -n $NS; " +
                "exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(11));

            output.WriteLine("Step 16: Verifying served cert is from Let's Encrypt...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway public-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
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

            // The starter template registers a single "apiservice" project with
            // WithExternalHttpEndpoints. The auto-router promotes the only external
            // frontend to the gateway root ("/") so the bare gateway URL just works,
            // i.e. /weatherforecast (not /apiservice/weatherforecast).
            output.WriteLine("Step 17: Verifying HTTPS /weatherforecast returns 200 (auto-routed at root)...");
            // On failure, dump gateway + httproute + service state so we can diagnose without re-deploying.
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway public-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "OK=0; for i in $(seq 1 30); do sleep 5; " +
                "S=$(curl -kso /dev/null -w '%{http_code}' -m 10 https://$FQDN/weatherforecast 2>/dev/null); " +
                "[ \"$S\" = \"200\" ] && echo \"HTTPS $S OK (auto-routed)\" && OK=1 && break; " +
                "echo \"Attempt $i: HTTPS $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || { " +
                "echo '=== Gateway describe ==='; kubectl describe gateway public-gw -n $NS; " +
                "echo '=== Gateway YAML ==='; kubectl get gateway public-gw -n $NS -o yaml; " +
                "echo '=== HTTPRoutes ==='; kubectl get httproute -n $NS -o yaml; " +
                "echo '=== Services ==='; kubectl get svc -n $NS; " +
                "echo '=== Pods ==='; kubectl get pods -n $NS; " +
                "echo 'FAIL: auto-routed HTTPS endpoint never returned 200'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            // Phase 1 (#17158) assertions: HTTP must 301 to HTTPS and HTTPS must carry HSTS.
            output.WriteLine("Step 18: Verifying HTTP → HTTPS 301 redirect (Phase 1)...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway public-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "STATUS=$(curl -so /dev/null -w '%{http_code}' -m 10 http://$FQDN/) && " +
                "LOC=$(curl -sI -m 10 http://$FQDN/ | tr -d '\\r' | awk -F': ' '/^[Ll]ocation:/{print $2}') && " +
                "echo \"HTTP status=$STATUS location=$LOC\" && " +
                "[ \"$STATUS\" = \"301\" ] && echo \"$LOC\" | grep -q '^https://' || { echo 'FAIL: expected 301 to https'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 19: Verifying HSTS header is present on HTTPS responses (Phase 1)...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway public-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "HSTS=$(curl -ksI -m 10 https://$FQDN/weatherforecast | tr -d '\\r' | awk -F': ' '/^[Ss]trict-[Tt]ransport-[Ss]ecurity:/{print $2}') && " +
                "echo \"HSTS=$HSTS\" && " +
                "echo \"$HSTS\" | grep -q 'max-age=' || { echo 'FAIL: HSTS header missing or malformed'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 20: Re-deploying to validate helm upgrade idempotency...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(20));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 21: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironment),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed - WithSimplifiedDeployment one-liner deployed AKS+AGC+cert-manager+gateway end-to-end!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployApiWithSimplifiedDeploymentToAzureKubernetesEnvironment),
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
