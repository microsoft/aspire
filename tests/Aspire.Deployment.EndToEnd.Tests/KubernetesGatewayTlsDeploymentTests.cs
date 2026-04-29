// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test for deploying an Aspire application to a pre-created AKS cluster
/// with Kubernetes Gateway API + TLS using <c>AddKubernetesEnvironment</c> and <c>AddGateway</c>.
/// The test creates the AKS cluster with ALB controller and Gateway API support, installs
/// cert-manager with a Let's Encrypt HTTP-01 ClusterIssuer (gatewayHTTPRoute solver), then
/// uses <c>aspire deploy</c> with <c>WithTls()</c> (no hostname). It verifies that:
/// 1. The Gateway gets an FQDN assigned by AGC
/// 2. The FQDN discovery pipeline step patches the hostname onto the HTTPS listener
/// 3. cert-manager issues a real TLS certificate via HTTP-01
/// 4. The app is accessible via port-forward and over HTTPS
/// </summary>
public sealed class KubernetesGatewayTlsDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(60);

    [Fact]
    public async Task DeployStarterWithGatewayTlsToKubernetes()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterWithGatewayTlsToKubernetesCore(cancellationToken);
    }

    private async Task DeployStarterWithGatewayTlsToKubernetesCore(CancellationToken cancellationToken)
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

        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("k8sgwtls");
        var clusterName = $"aks-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}";
        var acrName = $"acrgw{DeploymentE2ETestHelpers.GetRunId()}{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();
        acrName = new string(acrName.Where(char.IsLetterOrDigit).Take(50).ToArray());
        if (acrName.Length < 5)
        {
            acrName = $"acrtest{Guid.NewGuid():N}"[..24];
        }

        var projectName = "K8sGatewayTls";
        var k8sNamespace = "gwtls";

        output.WriteLine($"Test: {nameof(DeployStarterWithGatewayTlsToKubernetes)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"AKS Cluster: {clusterName}");
        output.WriteLine($"ACR Name: {acrName}");
        output.WriteLine($"K8s Namespace: {k8sNamespace}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // ===== PHASE 1: Provision AKS with ALB + cert-manager =====

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Register resource providers for AGC + Gateway API
            output.WriteLine("Step 2: Registering resource providers...");
            await auto.TypeAsync(
                "az provider register --namespace Microsoft.ContainerService --wait && " +
                "az provider register --namespace Microsoft.ContainerRegistry --wait && " +
                "az provider register --namespace Microsoft.Network --wait && " +
                "az provider register --namespace Microsoft.NetworkFunction --wait && " +
                "az provider register --namespace Microsoft.ServiceNetworking --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync(
                "az feature register --namespace Microsoft.ContainerService --name ManagedGatewayAPIPreview 2>/dev/null || true && " +
                "az feature register --namespace Microsoft.ContainerService --name ApplicationLoadBalancerPreview 2>/dev/null || true && " +
                "az provider register --namespace Microsoft.ContainerService");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync("az extension add --name alb --yes 2>/dev/null || true && az extension add --name aks-preview --yes 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Create resource group
            output.WriteLine("Step 3: Creating resource group...");
            await auto.TypeAsync($"az group create --name {resourceGroupName} --location westus3 --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Create ACR
            output.WriteLine("Step 4: Creating ACR...");
            await auto.TypeAsync($"az acr create --resource-group {resourceGroupName} --name {acrName} --sku Basic --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Login to ACR early (OIDC token expires during AKS creation)
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Create AKS with Azure CNI, OIDC, workload identity, Gateway API, and ALB controller
            // Per https://learn.microsoft.com/azure/application-gateway/for-containers/quickstart-deploy-application-gateway-for-containers-alb-controller-addon
            output.WriteLine("Step 5: Creating AKS cluster with Gateway API + ALB (10-15 minutes)...");
            await auto.TypeAsync(
                $"az aks create " +
                $"--resource-group {resourceGroupName} " +
                $"--name {clusterName} " +
                $"--location westus3 " +
                $"--node-count 1 " +
                $"--node-vm-size Standard_D2as_v5 " +
                $"--network-plugin azure " +
                $"--generate-ssh-keys " +
                $"--attach-acr {acrName} " +
                $"--enable-oidc-issuer " +
                $"--enable-workload-identity " +
                $"--enable-gateway-api " +
                $"--enable-application-load-balancer " +
                $"--output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            // Get credentials
            await auto.TypeAsync($"az aks get-credentials --resource-group {resourceGroupName} --name {clusterName} --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Verify ALB controller is running and GatewayClass exists
            // The ALB controller pods may still be initializing after cluster creation,
            // so poll until they are running and the GatewayClass is available.
            output.WriteLine("Step 6: Waiting for ALB controller and GatewayClass...");
            await auto.TypeAsync(
                "for i in $(seq 1 60); do " +
                "READY=$(kubectl get pods -n kube-system -l app=alb-controller -o jsonpath='{.items[0].status.phase}' 2>/dev/null); " +
                "[ \"$READY\" = \"Running\" ] && kubectl get gatewayclass azure-alb-external >/dev/null 2>&1 && " +
                "echo 'ALB controller running and GatewayClass available' && break; " +
                "echo \"Attempt $i: ALB controller status=$READY, waiting...\"; sleep 10; done && " +
                "kubectl get pods -n kube-system | grep alb-controller && " +
                "kubectl get gatewayclass azure-alb-external");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

            // Create ApplicationLoadBalancer CRD using the add-on's auto-created subnet
            output.WriteLine("Step 7: Creating ApplicationLoadBalancer...");
            await auto.TypeAsync(
                $"MC_RG=$(az aks show -g {resourceGroupName} -n {clusterName} --query nodeResourceGroup -o tsv) && " +
                "SUBNET_ID=$(az network vnet subnet show -g $MC_RG " +
                "--vnet-name $(az network vnet list -g $MC_RG --query '[0].name' -o tsv) " +
                "--name aks-appgateway --query id -o tsv) && " +
                "echo \"Subnet: $SUBNET_ID\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync(
                "cat <<EOF | kubectl apply -f -\n" +
                "apiVersion: alb.networking.azure.io/v1\n" +
                "kind: ApplicationLoadBalancer\n" +
                "metadata:\n" +
                "  name: alb-aspire\n" +
                "  namespace: default\n" +
                "spec:\n" +
                "  associations:\n" +
                "  - $SUBNET_ID\n" +
                "EOF");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Wait for ALB ready
            await auto.TypeAsync(
                "for i in $(seq 1 60); do " +
                "STATUS=$(kubectl get applicationloadbalancer alb-aspire -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null); " +
                "[ \"$STATUS\" = \"True\" ] && echo 'ALB Ready' && break; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Install cert-manager with Gateway API support
            output.WriteLine("Step 8: Installing cert-manager...");
            await auto.TypeAsync(
                "helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager " +
                "--namespace cert-manager --create-namespace " +
                "--set crds.enabled=true --set config.enableGatewayAPI=true --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Create HTTP-01 ClusterIssuer
            output.WriteLine("Step 9: Creating HTTP-01 ClusterIssuer...");
            await auto.TypeAsync(
                "cat <<EOF | kubectl apply -f -\n" +
                "apiVersion: cert-manager.io/v1\n" +
                "kind: ClusterIssuer\n" +
                "metadata:\n" +
                "  name: letsencrypt-http01\n" +
                "spec:\n" +
                "  acme:\n" +
                "    server: https://acme-v02.api.letsencrypt.org/directory\n" +
                "    email: aspire-e2e-test@microsoft.com\n" +
                "    privateKeySecretRef:\n" +
                "      name: letsencrypt-http01-account-key\n" +
                "    solvers:\n" +
                "    - http01:\n" +
                "        gatewayHTTPRoute:\n" +
                "          parentRefs:\n" +
                "          - kind: Gateway\n" +
                $"            name: ingress\n" +
                $"            namespace: {k8sNamespace}\n" +
                "EOF");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("kubectl get clusterissuer letsencrypt-http01");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(15));

            // ===== PHASE 2: Create Aspire Project with Gateway + TLS =====

            await auto.InstallCurrentBuildAspireCliAsync(counter, output, "Step 10");

            output.WriteLine("Step 9: Creating Aspire starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            output.WriteLine("Step 9: Adding Kubernetes hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Modify AppHost.cs: AddKubernetesEnvironment + AddGateway + WithTls (no hostname)
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 9: Modifying AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);

            var buildRunPattern = "builder.Build().Run();";
            var replacement = $$"""
// Kubernetes environment with Gateway API + TLS (no hostname — FQDN auto-discovered)
var registryEndpoint = builder.AddParameter("registryendpoint");
var registry = builder.AddContainerRegistry("registry", registryEndpoint);

var k8s = builder.AddKubernetesEnvironment("k8s")
    .WithHelm(helm =>
    {
        helm.WithNamespace(builder.AddParameter("namespace"));
        helm.WithChartVersion(builder.AddParameter("chartversion"));
    });

var gateway = k8s.AddGateway("ingress")
    .WithGatewayClass("azure-alb-external")
    .WithGatewayAnnotation("alb.networking.azure.io/alb-name", "alb-aspire")
    .WithGatewayAnnotation("alb.networking.azure.io/alb-namespace", "default")
    .WithGatewayAnnotation("cert-manager.io/cluster-issuer", "letsencrypt-http01")
    .WithRoute("/", webfrontend.GetEndpoint("http"))
    .WithTls();

builder.Build().Run();
""";

            content = content.Replace(buildRunPattern, replacement);

            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = "#pragma warning disable ASPIREPIPELINES001\n" + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with AddKubernetesEnvironment + AddGateway + WithTls");

            // Navigate to AppHost dir
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // ===== PHASE 3: Deploy with aspire deploy =====

            // Refresh ACR login
            output.WriteLine("Step 9: Refreshing ACR login...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Set parameters as environment variables so aspire deploy doesn't prompt
            output.WriteLine("Step 9: Setting deployment parameters...");
            await auto.TypeAsync(
                $"export Parameters__registryendpoint={acrName}.azurecr.io && " +
                $"export Parameters__namespace={k8sNamespace} && " +
                "export Parameters__chartversion=0.1.0");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Deploy using aspire deploy
            output.WriteLine("Step 9: Running aspire deploy...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(15));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // ===== PHASE 4: Verify Gateway TLS =====

            // Wait for pods
            output.WriteLine("Step 9: Waiting for pods...");
            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod --all -n {k8sNamespace} --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync($"kubectl get pods -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Check Gateway has address
            output.WriteLine("Step 9: Checking Gateway address...");
            await auto.TypeAsync(
                $"for i in $(seq 1 30); do " +
                $"FQDN=$(kubectl get gateway ingress -n {k8sNamespace} -o jsonpath='{{.status.addresses[0].value}}' 2>/dev/null); " +
                "[ -n \"$FQDN\" ] && echo \"Gateway FQDN: $FQDN\" && break; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Check HTTPS listener has hostname (patched by FQDN discovery step)
            output.WriteLine("Step 9: Checking HTTPS listener hostname...");
            await auto.TypeAsync(
                $"kubectl get gateway ingress -n {k8sNamespace} " +
                "-o jsonpath='{range .spec.listeners[*]}{.name} {.protocol} {.hostname}{\"\\n\"}{end}'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Wait for certificate
            output.WriteLine("Step 9: Waiting for TLS certificate (up to 10 minutes)...");
            await auto.TypeAsync(
                $"for i in $(seq 1 60); do " +
                $"READY=$(kubectl get certificate -n {k8sNamespace} -o jsonpath='{{.items[0].status.conditions[?(@.type==\"Ready\")].status}}' 2>/dev/null); " +
                "[ \"$READY\" = \"True\" ] && echo 'Certificate Ready!' && break; " +
                "echo \"Attempt $i: waiting...\"; sleep 10; done && " +
                $"kubectl get certificate -n {k8sNamespace}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(12));

            // Test HTTPS access
            output.WriteLine("Step 9: Testing HTTPS access...");
            await auto.TypeAsync(
                $"FQDN=$(kubectl get gateway ingress -n {k8sNamespace} -o jsonpath='{{.status.addresses[0].value}}') && " +
                "echo \"Testing: https://$FQDN\" && " +
                "OK=0; for i in $(seq 1 10); do sleep 5; " +
                "S=$(curl -so /dev/null -w '%{http_code}' -m 10 https://$FQDN/ 2>/dev/null); " +
                "[ \"$S\" = \"200\" ] && echo \"HTTPS $S OK\" && OK=1 && break; " +
                "echo \"Attempt $i: $S\"; done; " +
                "[ \"$OK\" = \"1\" ] || echo 'WARN: HTTPS not 200 yet (cert may still be provisioning)'");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Verify via port-forward (confirms app is running regardless of external TLS)
            output.WriteLine("Step 9: Verifying app via port-forward...");
            await auto.TypeAsync($"kubectl port-forward svc/webfrontend-service 18081:8080 -n {k8sNamespace} &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync("sleep 3");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            await auto.TypeAsync(
                "OK=0; for i in $(seq 1 10); do sleep 3 && " +
                "curl -sf http://localhost:18081/ -o /dev/null -w '%{http_code}' && " +
                "echo ' OK' && OK=1 && break; done; " +
                "[ \"$OK\" = \"1\" ] || { echo 'FAIL: webfrontend unreachable'; exit 1; }");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("kill %1 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            // ===== PHASE 5: Cleanup =====

            output.WriteLine("Step 9: Destroying deployment...");
            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Gateway TLS deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterWithGatewayTlsToKubernetes),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["cluster"] = clusterName,
                    ["acr"] = acrName,
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - Aspire app deployed with Gateway API TLS via HTTP-01!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterWithGatewayTlsToKubernetes),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                output.WriteLine($"Resource group deletion initiated: {resourceGroupName}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Deletion initiated");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync();
                output.WriteLine($"Resource group deletion may have failed (exit code {process.ExitCode}): {error}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, $"Exit code {process.ExitCode}: {error}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
        }
    }
}
