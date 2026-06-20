// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// TypeScript-AppHost variant of the AKS cert-manager E2E test. Mirrors
/// <see cref="AksAzureKubernetesEnvironmentCertManagerDeploymentTests"/> but uses the
/// TypeScript Express/React starter (<c>aspire new</c> --&gt; <c>Starter App (Express/React, TypeScript AppHost)</c>)
/// and patches <c>apphost.mts</c> to wire <c>addAzureKubernetesEnvironment</c> +
/// <c>addCertManager</c> + <c>addIssuer().withLetsEncryptProductionParam(acmeEmail).withHttp01Solver()</c>
/// + <c>gateway.withGatewayTlsIssuer(letsEncrypt)</c> around the Express API.
///
/// Proves that the cert-manager API surface generated for TypeScript via <c>[AspireExport]</c>
/// works end-to-end against a real AKS cluster (this complements the polyglot type-check
/// validation in <c>tests/PolyglotAppHosts/Aspire.Hosting.Kubernetes/TypeScript/apphost.mts</c>).
///
/// Uses Let's Encrypt <em>production</em> so the served cert chains to a publicly-trusted root
/// (mirrors a realistic deployment). Production is rate-limited (50 certs / registered
/// domain / week, 5 duplicate certs / week — see https://letsencrypt.org/docs/rate-limits/),
/// so re-runs that change the gateway FQDN consume quota; throttle CI scheduling
/// accordingly.
/// </summary>
public sealed class AksAzureKubernetesEnvironmentCertManagerTypeScriptDeploymentTests(ITestOutputHelper output)
{
    // Same budget as the C# variant — AKS + AGC + cert-manager bring-up plus ACME challenge
    // can take 25-30 minutes in the worst case.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(75);

    [Fact]
    public async Task DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironmentCore(cancellationToken);
    }

    private async Task DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironmentCore(CancellationToken cancellationToken)
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
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akscmts");
        var projectName = "AksCertManagerTs";

        // ACME registration email — used for Let's Encrypt account binding and renewal warnings.
        const string acmeEmail = "aspire-e2e-test@microsoft.com";

        output.WriteLine($"Test: {nameof(DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment)}");
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

            // TypeScript apphosts need the full bundle (not just the CLI binary) because the
            // prebuilt AppHost server is required for `aspire add` to regenerate SDK code.
            output.WriteLine("Step 2: Installing Aspire CLI bundle...");
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);

            output.WriteLine("Step 3: Creating TypeScript Express/React project...");
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 5: Adding Azure Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();

            // aspire add may or may not show a version selection prompt depending on whether
            // packages are available from the local hive (bundle install).
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                await auto.WaitForAspireAddCompletionAsync(counter);
            }
            else
            {
                await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));
            }

            // Patch apphost.mts. The Express/React starter creates an Express API at ./api
            // exposing an HTTP endpoint, plus a Vite frontend bundled in for publish. We
            // replace the trailing `await builder.build().run();` with the AKS + cert-manager
            // wiring that puts the API behind a Gateway with TLS issued by Let's Encrypt production.
            //
            // The TypeScript surface mirrors the C# API exposed via [AspireExport]:
            //   addAzureKubernetesEnvironment / withSubnet / withSystemNodePool / addNodePool
            //   addLoadBalancer
            //   addCertManager / addIssuer / withLetsEncryptProductionParam / withHttp01Solver
            //   addGateway / withLoadBalancer / withRoute / withGatewayTlsIssuer
            //   publishAsKubernetesService / addManifest
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            // The TypeScript starter templates (including Express/React) emit apphost.mts
            // since PR #16984. Reading apphost.ts here would fail with FileNotFoundException.
            var appHostFilePath = Path.Combine(projectDir, "apphost.mts");

            output.WriteLine($"Step 6: Modifying apphost.mts at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            const string buildRunPattern = "await builder.build().run();";
            const string replacement = """
// VNet layout chosen to avoid the AKS default service CIDR (10.0.0.0/16):
//   10.100.0.0/16   - vnet
//     10.100.0.0/22 - aks node pool subnet
//     10.100.4.0/24 - AGC frontend subnet (delegated to ServiceNetworking by addLoadBalancer)
const vnet = await builder.addAzureVirtualNetwork("vnet", { addressPrefix: "10.100.0.0/16" });
const aksSubnet = await vnet.addSubnet("aks-nodes", "10.100.0.0/22");
const albSubnet = await vnet.addSubnet("alb-public", "10.100.4.0/24");

const aks = await builder.addAzureKubernetesEnvironment("aks");
await aks.withSubnet(aksSubnet);
await aks.withSystemNodePool({ vmSize: "Standard_D2as_v5" });
await aks.addNodePool("workload", { vmSize: "Standard_D2as_v5", minCount: 1, maxCount: 3 });

const publicLb = await aks.addLoadBalancer("public", albSubnet);

// Email registered with the ACME endpoint. Passed as a parameter so the test
// supplies it via Parameters__acmeemail without burning it into apphost source.
const acmeEmail = await builder.addParameter("acmeemail");

// Install cert-manager via the typed API and declare a Let's Encrypt PRODUCTION ClusterIssuer.
const certManager = await aks.addCertManager("cert-manager");
const letsEncrypt = await certManager.addIssuer("letsencrypt-prod");
await letsEncrypt.withLetsEncryptProductionParam(acmeEmail);
await letsEncrypt.withHttp01Solver();

// Gateway with HTTPS listener. withGatewayTlsIssuer(letsEncrypt) creates the listener AND
// adds the cert-manager.io/cluster-issuer annotation in one call.
const gateway = await aks.addGateway("api-gw");
await gateway.withLoadBalancer(publicLb);
await gateway.withGatewayPathRoute("/", app.getEndpoint("http"));
await gateway.withGatewayTlsIssuer(letsEncrypt);

// A second resource validates the generic Kubernetes service/custom-manifest publish
// surface from TypeScript without adding another full AKS deployment test.
const serviceContainer = await builder.addContainer("kube-service", "redis:alpine");
await serviceContainer.withEndpoint({ name: "tcp", targetPort: 6379 });
await serviceContainer.withComputeEnvironment(aks);
await serviceContainer.publishAsKubernetesService(async (service) => {
    await service.addManifest("v1", "ConfigMap", "kube-service-config", {
        configure: async (manifest) => {
            await manifest
                .withLabel("example.com/source", "typescript")
                .withAnnotation("example.com/coverage", "deployment-e2e")
                .withField("data.coverage", "typescript-kubernetes-service");
        },
    });
});

await builder.build().run();
""";

            content = content.Replace(buildRunPattern, replacement);
            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified apphost.mts with addCertManager + addIssuer + withGatewayTlsIssuer");

            output.WriteLine("Step 7: Setting deployment environment variables...");
            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && " +
                $"export AZURE__LOCATION=westus3 && " +
                $"export AZURE__RESOURCEGROUP={resourceGroupName} && " +
                $"export Parameters__acmeemail={acmeEmail}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The deploy pipeline provisions AKS + AGC, installs the cert-manager Helm chart,
            // applies the ClusterIssuer, and pre-creates the bootstrap TLS secret. Use a
            // generous timeout to cover the AKS+AGC bring-up window.
            output.WriteLine("Step 8: Starting AKS + cert-manager deployment (15-20 min)...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(40));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 9: Getting AKS credentials...");
            await auto.TypeAsync(
                $"AKS_NAME=$(az aks list -g {resourceGroupName} --query '[0].name' -o tsv) && " +
                $"az aks get-credentials -g {resourceGroupName} -n $AKS_NAME --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 10: Waiting for pods to be ready...");
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

            output.WriteLine("Step 11: Verifying ClusterIssuer is Ready...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 30); do " +
                "READY=$(kubectl get clusterissuer letsencrypt-prod -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'ClusterIssuer Ready' && OK=1 && break; " +
                "echo \"Attempt $i: ClusterIssuer status=$READY, waiting...\"; sleep 5; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: letsencrypt-prod ClusterIssuer never became Ready'; kubectl describe clusterissuer letsencrypt-prod; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 12: Discovering gateway namespace...");
            await auto.TypeAsync(
                "NS=$(kubectl get gateway --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"api-gw\")]}{.metadata.namespace}{end}') && " +
                "echo \"Namespace: $NS\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 13: Verifying TypeScript publishAsKubernetesService custom manifest...");
            await auto.TypeAsync(
                "SVC_NS=$(kubectl get svc --all-namespaces -o jsonpath='{range .items[?(@.metadata.name==\"kube-service-service\")]}{.metadata.namespace}{end}') && " +
                "[ -n \"$SVC_NS\" ] || { echo 'FAIL: kube-service-service service was not created'; kubectl get svc --all-namespaces; exit 1; } && " +
                "echo \"Service namespace: $SVC_NS\" && " +
                "kubectl get svc kube-service-service -n $SVC_NS && " +
                "COVERAGE=$(kubectl get configmap kube-service-config -n $SVC_NS -o jsonpath='{.data.coverage}' 2>/dev/null) && " +
                "[ \"$COVERAGE\" = \"typescript-kubernetes-service\" ] || { echo \"FAIL: kube-service-config coverage was '$COVERAGE'\"; kubectl get configmap -n $SVC_NS; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 14: Waiting for AGC to assign gateway FQDN (up to 15 min)...");
            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 90); do " +
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}' 2>/dev/null); " +
                "[ -n \"$FQDN\" ] && echo \"Gateway FQDN: $FQDN\" && OK=1 && break; " +
                "echo \"Attempt $i: waiting for AGC FQDN...\"; sleep 10; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: gateway never received AGC FQDN'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(16));

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
            // Production certs identify as "Let's Encrypt" (no "(STAGING)" prefix). Using
            // openssl s_client (with -servername for SNI) rather than curl — both work for
            // production certs, but openssl gives us issuer-string asserts without depending
            // on system trust store configuration. AGC takes a few seconds to load the new
            // cert into the data plane after the secret is updated, so we retry the probe.
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

            // Verify HTTPS actually serves the Express API. Production certs chain to a
            // publicly-trusted root, but we still pass -k to curl for resilience against
            // transient trust-store quirks on the runner; the previous step already proved
            // cryptographic identity (issuer == Let's Encrypt). The Express API serves at
            // "/" — any 2xx response is a pass.
            output.WriteLine("Step 17: Verifying https://<fqdn>/ returns 2xx from the Express API...");
            await auto.TypeAsync(
                "FQDN=$(kubectl get gateway api-gw -n $NS -o jsonpath='{.status.addresses[0].value}') && " +
                "OK=0; for i in $(seq 1 30); do sleep 5; " +
                "S=$(curl -kso /dev/null -w '%{http_code}' -m 10 https://$FQDN/ 2>/dev/null); " +
                "case \"$S\" in 2*) echo \"HTTPS $S OK\"; OK=1; break;; esac; " +
                "echo \"Attempt $i: HTTPS $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: HTTPS endpoint never returned 2xx'; exit 1; }");
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
                nameof(DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed - cert-manager issued a Let's Encrypt cert via HTTP-01 (TypeScript apphost)!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployTypeScriptApiWithCertManagerToAzureKubernetesEnvironment),
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
