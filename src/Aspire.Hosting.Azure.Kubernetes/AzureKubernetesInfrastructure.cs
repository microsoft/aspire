// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline types are experimental
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource is experimental

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Infrastructure eventing subscriber that processes compute resources
/// targeting an AKS environment.
/// </summary>
internal sealed class AzureKubernetesInfrastructure(
    ILogger<AzureKubernetesInfrastructure> logger)
    : IDistributedApplicationEventingSubscriber
{
    /// <inheritdoc />
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (!executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        }

        return Task.CompletedTask;
    }

    private Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        var aksEnvironments = @event.Model.Resources
            .OfType<AzureKubernetesEnvironmentResource>()
            .ToArray();

        if (aksEnvironments.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var environment in aksEnvironments)
        {
            logger.LogInformation("Processing AKS environment '{Name}'", environment.Name);

            // Add a pipeline step to fetch AKS credentials into an isolated kubeconfig
            // file. This runs after AKS is provisioned and before the Helm deploy.
            AddGetCredentialsStep(environment);

            // Ensure a default user node pool exists for workload scheduling.
            // The system pool should only run system pods; application workloads
            // need a user pool.
            var defaultUserPool = EnsureDefaultUserNodePool(environment);

            foreach (var r in @event.Model.GetComputeResources())
            {
                var resourceComputeEnvironment = r.GetComputeEnvironment();

                // Check if this resource targets THIS AKS environment
                if (resourceComputeEnvironment is not null && resourceComputeEnvironment != environment)
                {
                    continue;
                }

                // If the resource has no explicit node pool affinity, assign it
                // to the default user pool.
                if (!r.TryGetLastAnnotation<AksNodePoolAffinityAnnotation>(out _) && defaultUserPool is not null)
                {
                    r.Annotations.Add(new AksNodePoolAffinityAnnotation(defaultUserPool));
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Ensures the AKS environment has at least one user node pool. If none exists,
    /// creates a default "workload" user pool.
    /// </summary>
    private static AksNodePoolResource? EnsureDefaultUserNodePool(AzureKubernetesEnvironmentResource environment)
    {
        var hasUserPool = environment.NodePools.Any(p => p.Mode is AksNodePoolMode.User);

        if (hasUserPool)
        {
            // Return the first user pool as the default for unaffinitized workloads.
            // Look for an existing AksNodePoolResource child that matches.
            var firstUserConfig = environment.NodePools.First(p => p.Mode is AksNodePoolMode.User);
            return FindNodePoolResource(environment, firstUserConfig.Name);
        }

        // No user pool configured — create a default one.
        var defaultConfig = new AksNodePoolConfig("workload", "Standard_D4s_v5", 1, 10, AksNodePoolMode.User);
        environment.NodePools.Add(defaultConfig);

        var defaultPool = new AksNodePoolResource("workload", defaultConfig, environment);
        return defaultPool;
    }

    private static AksNodePoolResource? FindNodePoolResource(AzureKubernetesEnvironmentResource environment, string poolName)
    {
        return new AksNodePoolResource(poolName,
            environment.NodePools.First(p => p.Name == poolName),
            environment);
    }

    /// <summary>
    /// Adds a pipeline step to the inner KubernetesEnvironmentResource that fetches
    /// AKS cluster credentials into an isolated kubeconfig file after the AKS cluster
    /// is provisioned via Bicep.
    /// </summary>
    private static void AddGetCredentialsStep(AzureKubernetesEnvironmentResource environment)
    {
        var k8sEnv = environment.KubernetesEnvironment;

        k8sEnv.Annotations.Add(new PipelineStepAnnotation((_) =>
        {
            var step = new PipelineStep
            {
                Name = $"aks-get-credentials-{environment.Name}",
                Description = $"Fetches AKS credentials for {environment.Name}",
                Action = ctx => GetAksCredentialsAsync(ctx, environment)
            };

            // Run after ALL Azure infrastructure is provisioned (including the AKS cluster).
            // This depends on the aggregation step that gates on all individual provision-* steps.
            step.DependsOn(AzureEnvironmentResource.ProvisionInfrastructureStepName);

            // Must complete before Helm prepare step
            step.RequiredBy($"prepare-{k8sEnv.Name}");

            return new[] { step };
        }));
    }

    /// <summary>
    /// Fetches AKS credentials into an isolated kubeconfig file using az aks get-credentials,
    /// then sets the KubeConfigPath on the inner KubernetesEnvironmentResource so that
    /// subsequent Helm and kubectl commands target the AKS cluster.
    /// </summary>
    private static async Task GetAksCredentialsAsync(
        PipelineStepContext context,
        AzureKubernetesEnvironmentResource environment)
    {
        var getCredsTask = await context.ReportingStep.CreateTaskAsync(
            $"Fetching AKS credentials for {environment.Name}",
            context.CancellationToken).ConfigureAwait(false);

        await using (getCredsTask.ConfigureAwait(false))
        {
            try
            {
                // The cluster name is the resource name — we set it directly in the Bicep template.
                // We don't use NameOutputReference.GetValueAsync() because it triggers parameter
                // resolution that may not be available at this point in the pipeline.
                var clusterName = environment.Name;

                // Get the resource group from the deployment state. The deployment state
                // is loaded into IConfiguration by the deploy-prereq step and contains
                // Azure:ResourceGroup from the provisioning context.
                // We read it via IConfiguration which is populated from the deployment
                // state JSON file before pipeline steps execute.
                var azPath = FindAzCli();
                var resourceGroup = await GetResourceGroupAsync(azPath, clusterName, context)
                    .ConfigureAwait(false);

                // Write credentials to an isolated kubeconfig file
                var kubeConfigDir = Directory.CreateTempSubdirectory("aspire-aks");
                var kubeConfigPath = Path.Combine(kubeConfigDir.FullName, "kubeconfig");

                var arguments = $"aks get-credentials --resource-group \"{resourceGroup}\" --name \"{clusterName}\" --file \"{kubeConfigPath}\" --overwrite-existing";

                context.Logger.LogInformation(
                    "Fetching AKS credentials: cluster={ClusterName}, resourceGroup={ResourceGroup}",
                    clusterName, resourceGroup);

                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = azPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

                await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    context.Logger.LogDebug("az (stdout): {Output}", stdout);
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    context.Logger.LogDebug("az (stderr): {Error}", stderr);
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"az aks get-credentials failed (exit code {process.ExitCode}): {stderr.Trim()}");
                }

                // Set the kubeconfig path on the inner K8s environment so
                // Helm and kubectl commands use --kubeconfig to target this cluster
                environment.KubernetesEnvironment.KubeConfigPath = kubeConfigPath;

                context.Logger.LogInformation(
                    "AKS credentials written to {KubeConfigPath}", kubeConfigPath);

                // Add AKS connection info to the pipeline summary
                context.Summary.Add(
                    "☸ AKS Cluster",
                    new MarkdownString($"**{clusterName}** in resource group **{resourceGroup}**"));

                context.Summary.Add(
                    "🔑 Connect to cluster",
                    new MarkdownString($"`az aks get-credentials --resource-group {resourceGroup} --name {clusterName}`"));

                await getCredsTask.SucceedAsync(
                    $"AKS credentials fetched for cluster {clusterName}",
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await getCredsTask.FailAsync(
                    $"Failed to fetch AKS credentials: {ex.Message}",
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static string FindAzCli()
    {
        // Check PATH first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var azNames = OperatingSystem.IsWindows()
            ? new[] { "az.CMD", "az.cmd", "az.exe" }
            : new[] { "az" };

        foreach (var dir in pathDirs)
        {
            foreach (var azName in azNames)
            {
                var candidate = Path.Combine(dir, azName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Check common Windows locations
        if (OperatingSystem.IsWindows())
        {
            var commonPaths = new[]
            {
                @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.CMD",
                @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.CMD",
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        throw new InvalidOperationException(
            "Azure CLI (az) not found. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli");
    }

    /// <summary>
    /// Gets the resource group, trying deployment state first, falling back to az CLI query.
    /// On first deploy, the deployment state may not be loaded into IConfiguration yet
    /// because it's written during the pipeline run (after create-provisioning-context).
    /// </summary>
    private static async Task<string> GetResourceGroupAsync(
        string azPath,
        string clusterName,
        PipelineStepContext context)
    {
        // Try deployment state first (works on re-deploys)
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var resourceGroup = configuration["Azure:ResourceGroup"];

        if (!string.IsNullOrEmpty(resourceGroup))
        {
            return resourceGroup;
        }

        // Fallback for first deploy: query Azure directly
        context.Logger.LogDebug(
            "Resource group not in deployment state, querying Azure for cluster '{ClusterName}'",
            clusterName);

        var arguments = $"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"{clusterName}\" --query [0].resourceGroup -o tsv";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        await process.StandardError.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);

        await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

        resourceGroup = stdout.Trim().ReplaceLineEndings("").Trim();

        if (string.IsNullOrEmpty(resourceGroup))
        {
            throw new InvalidOperationException(
                $"Could not resolve resource group for AKS cluster '{clusterName}'. " +
                "Ensure Azure provisioning has completed.");
        }

        return resourceGroup;
    }
}
