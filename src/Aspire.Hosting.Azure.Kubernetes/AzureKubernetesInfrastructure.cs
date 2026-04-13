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

            // Flow the container registry to the inner K8s environment so
            // KubernetesInfrastructure can find it for image push/pull.
            FlowContainerRegistry(environment, @event.Model);

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

                // NOTE: We do NOT add DeploymentTargetAnnotation here.
                // The inner KubernetesEnvironmentResource is in the model, so
                // KubernetesInfrastructure will handle Helm chart generation
                // and add the DeploymentTargetAnnotation with the correct
                // KubernetesResource deployment target.
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
    /// Flows the container registry from the AKS environment to the inner
    /// KubernetesEnvironmentResource via ContainerRegistryReferenceAnnotation.
    /// This allows KubernetesInfrastructure to discover the registry for image push/pull.
    /// </summary>
    private static void FlowContainerRegistry(AzureKubernetesEnvironmentResource environment, DistributedApplicationModel _)
    {
        IContainerRegistry? registry = null;

        // Check for explicit registry set via WithContainerRegistry
        if (environment.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            registry = annotation.Registry;
        }
        else if (environment.DefaultContainerRegistry is not null)
        {
            registry = environment.DefaultContainerRegistry;
        }

        if (registry is not null)
        {
            // Propagate to the inner K8s environment so KubernetesInfrastructure finds it
            environment.KubernetesEnvironment.Annotations.Add(
                new ContainerRegistryReferenceAnnotation(registry));
        }
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

            // Run after AKS cluster is provisioned
            step.DependsOn($"provision-{environment.Name}");

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

                // Get the resource group from Azure provisioning configuration.
                // The create-provisioning-context step resolves this and stores it in
                // the Azure:ResourceGroup config key before any provision steps run.
                var configuration = context.Services.GetRequiredService<IConfiguration>();
                var resourceGroup = configuration["Azure:ResourceGroup"]
                    ?? throw new InvalidOperationException(
                        "Azure resource group name not found in configuration. " +
                        "Ensure the Azure provisioning context has been created.");

                // Write credentials to an isolated kubeconfig file
                var kubeConfigDir = Directory.CreateTempSubdirectory("aspire-aks");
                var kubeConfigPath = Path.Combine(kubeConfigDir.FullName, "kubeconfig");

                var arguments = $"aks get-credentials --resource-group {resourceGroup} --name {clusterName} --file \"{kubeConfigPath}\" --overwrite-existing";

                context.Logger.LogInformation("Fetching AKS credentials: az {Arguments}", arguments);

                var azPath = FindAzCli();

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
}
