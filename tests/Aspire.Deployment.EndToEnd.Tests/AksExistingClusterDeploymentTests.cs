// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AksExistingClusterDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployToCliCreatedClusterUsingResourceIdParameter()
    {
        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);
        using var workspace = TemporaryWorkspace.Create(output);
        var clusterResourceGroup = DeploymentE2ETestHelpers.GenerateResourceGroupName("aks-existing-cluster");
        var deploymentResourceGroup = DeploymentE2ETestHelpers.GenerateResourceGroupName("aks-existing-app");
        var startTime = DateTime.UtcNow;
        const string projectName = "ExistingAks";

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellation.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromMinutes(5));
            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            await auto.RunCommandAsync(
                $"export TEST_SUBSCRIPTION={AspireCliShellCommandHelpers.QuoteBashArg(subscriptionId)} " +
                $"CLUSTER_RG={AspireCliShellCommandHelpers.QuoteBashArg(clusterResourceGroup)} " +
                $"DEPLOY_RG={AspireCliShellCommandHelpers.QuoteBashArg(deploymentResourceGroup)} " +
                $"BOOTSTRAP_DIR={AspireCliShellCommandHelpers.QuoteBashArg(workspace.WorkspaceRoot.FullName)}",
                counter);

            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, """
                set -euo pipefail
                az group create --subscription "$TEST_SUBSCRIPTION" --name "$CLUSTER_RG" --location westus3 -o none
                az group create --subscription "$TEST_SUBSCRIPTION" --name "$DEPLOY_RG" --location westus3 -o none
                ssh-keygen -t rsa -b 2048 -N '' -f "$BOOTSTRAP_DIR/aks-key"
                # New AKS 1.34+ clusters enable OIDC by default and cannot disable it afterward.
                # Use the last pre-default version with LTS to exercise a cluster without OIDC.
                # https://learn.microsoft.com/azure/aks/use-oidc-issuer
                az aks create --subscription "$TEST_SUBSCRIPTION" --resource-group "$CLUSTER_RG" \
                    --name existing-aks --location westus3 --nodepool-name system --node-count 1 \
                    --node-vm-size Standard_D2as_v5 --enable-managed-identity \
                    --kubernetes-version 1.33 --tier premium --k8s-support-plan AKSLongTermSupport \
                    --ssh-key-value "$BOOTSTRAP_DIR/aks-key.pub" \
                    --query id -o tsv > "$BOOTSTRAP_DIR/cluster-id"
                az aks show --subscription "$TEST_SUBSCRIPTION" --resource-group "$CLUSTER_RG" --name existing-aks \
                    -o json | jq -S '{id, agentPoolProfiles, networkProfile, oidcIssuerProfile, securityProfile, identityProfile, tags}' \
                    > "$BOOTSTRAP_DIR/cluster-before.json"
                jq -e '.oidcIssuerProfile.enabled != true and .securityProfile.workloadIdentity.enabled != true' \
                    "$BOOTSTRAP_DIR/cluster-before.json"
                """, TimeSpan.FromMinutes(25));

            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);
            await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);
            await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.Kubernetes");

            var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, $"{projectName}.AppHost", "AppHost.cs");
            File.WriteAllText(appHostPath, """
                #pragma warning disable ASPIREAZURE003
                #pragma warning disable ASPIRECOMPUTE003
                using Azure.Core;

                var builder = DistributedApplication.CreateBuilder(args);
                var resourceId = builder.AddParameter("aksResourceId");
                var clusterId = new ResourceIdentifier(
                    await resourceId.Resource.GetValueAsync(default)
                    ?? throw new InvalidOperationException("The AKS resource ID parameter is required."));
                if (clusterId.ResourceType != new ResourceType("Microsoft.ContainerService/managedClusters") ||
                    clusterId.ResourceGroupName is not { Length: > 0 } resourceGroup ||
                    clusterId.SubscriptionId is not { Length: > 0 } subscription)
                {
                    throw new InvalidOperationException("The resource ID must identify an AKS managed cluster in a subscription and resource group.");
                }

                var aks = builder.AddAzureKubernetesEnvironment("aks")
                    .AsExistingInResourceGroup(clusterId.Name, resourceGroup, subscription);
                builder.AddDotnetProject("apiservice", "../ExistingAks.ApiService/ExistingAks.ApiService.csproj")
                    .WithComputeEnvironment(aks);

                builder.Build().Run();
                """);

            // Pass the complete ID returned by Azure CLI, not a separately supplied cluster name.
            await auto.RunCommandAsync(
                "unset ASPIRE_PLAYGROUND Azure__Location Azure__ResourceGroup Azure__SubscriptionId && " +
                "export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP=\"$DEPLOY_RG\" AZURE__SUBSCRIPTIONID=\"$TEST_SUBSCRIPTION\" " +
                "Parameters__aksResourceId=\"$(cat \"$BOOTSTRAP_DIR/cluster-id\")\" " +
                "KUBECONFIG=\"$BOOTSTRAP_DIR/ambient-kubeconfig\"",
                counter);
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(TimeSpan.FromMinutes(25), counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, """
                set -euxo pipefail
                az aks show --subscription "$TEST_SUBSCRIPTION" --resource-group "$CLUSTER_RG" --name existing-aks -o json \
                    | jq -S '{id, agentPoolProfiles, networkProfile, oidcIssuerProfile, securityProfile, identityProfile, tags}' \
                    > "$BOOTSTRAP_DIR/cluster-after.json"
                diff -u "$BOOTSTRAP_DIR/cluster-before.json" "$BOOTSTRAP_DIR/cluster-after.json"
                test "$(az aks list --subscription "$TEST_SUBSCRIPTION" --resource-group "$DEPLOY_RG" --query 'length(@)' -o tsv)" = 0
                # Azure CLI and Bicep can use different casing for the same ARM resource ID.
                az deployment group list --subscription "$TEST_SUBSCRIPTION" --resource-group "$DEPLOY_RG" \
                    --query '[].name' -o tsv | while IFS= read -r DEPLOYMENT; do
                    az deployment group show --subscription "$TEST_SUBSCRIPTION" --resource-group "$DEPLOY_RG" \
                        --name "$DEPLOYMENT" --query properties.outputs.id.value -o tsv
                done | grep -Fxi "$Parameters__aksResourceId"
                az aks get-credentials --subscription "$TEST_SUBSCRIPTION" --resource-group "$CLUSTER_RG" \
                    --name existing-aks --file "$BOOTSTRAP_DIR/verification-kubeconfig"
                export KUBECONFIG="$BOOTSTRAP_DIR/verification-kubeconfig"
                NS=$(kubectl get services --all-namespaces -o json \
                    | jq -er '.items[] | select(.metadata.name == "apiservice-service") | .metadata.namespace')
                printf '%s' "$NS" > "$BOOTSTRAP_DIR/namespace"
                kubectl wait --for=condition=Available deployment --all -n "$NS" --timeout=300s
                kubectl port-forward -n "$NS" svc/apiservice-service 18080:8080 > "$BOOTSTRAP_DIR/port-forward.log" 2>&1 &
                FORWARD_PID=$!
                trap 'kill "$FORWARD_PID"; wait "$FORWARD_PID" 2>/dev/null || true' EXIT
                if ! curl --fail --retry 20 --retry-all-errors --retry-delay 2 --max-time 10 \
                    http://localhost:18080/weatherforecast -o "$BOOTSTRAP_DIR/response.json"; then
                    cat "$BOOTSTRAP_DIR/port-forward.log"
                    exit 1
                fi
                jq -e 'type == "array" and length == 5' "$BOOTSTRAP_DIR/response.json"
                """, TimeSpan.FromMinutes(8));

            // Destroy must reacquire isolated credentials; the caller's context remains empty.
            await auto.AspireDestroyAsync(counter, TimeSpan.FromMinutes(15));
            await DotnetProjectDeploymentHelpers.RunScriptAsync(auto, counter, """
                set -euo pipefail
                test "$(az aks show --subscription "$TEST_SUBSCRIPTION" --resource-group "$CLUSTER_RG" --name existing-aks --query id -o tsv)" = "$Parameters__aksResourceId"
                export KUBECONFIG="$BOOTSTRAP_DIR/verification-kubeconfig"
                NS=$(cat "$BOOTSTRAP_DIR/namespace")
                kubectl get deployments --all-namespaces -o json \
                    | jq -e --arg namespace "$NS" 'all(.items[]; .metadata.namespace != $namespace)'
                """, TimeSpan.FromMinutes(2));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployToCliCreatedClusterUsingResourceIdParameter),
                deploymentResourceGroup,
                new Dictionary<string, string>(),
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployToCliCreatedClusterUsingResourceIdParameter), deploymentResourceGroup, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            await DotnetProjectDeploymentHelpers.CleanupAsync(deploymentResourceGroup, subscriptionId, output);
            await DotnetProjectDeploymentHelpers.CleanupAsync(clusterResourceGroup, subscriptionId, output);
        }
    }
}
