// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline step types used for push/deploy dependency wiring
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource.ProvisionInfrastructureStepName for pipeline ordering
#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService/TempDirectory are experimental

using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;
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
internal sealed partial class AzureKubernetesInfrastructure(
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
            var defaultUserPool = EnsureDefaultUserNodePool(environment, @event.Model);

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
                if (!r.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out _) && defaultUserPool is not null)
                {
                    r.Annotations.Add(new KubernetesNodePoolAnnotation(defaultUserPool));
                }

                // Wire workload identity: if the resource has an AppIdentityAnnotation
                // (auto-created by AzureResourcePreparer or explicit via WithAzureUserAssignedIdentity),
                // generate a ServiceAccount and wire the pod spec.
                if (r.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentity))
                {
                    // Ensure OIDC + workload identity are enabled on the cluster
                    environment.OidcIssuerEnabled = true;
                    environment.WorkloadIdentityEnabled = true;

                    var saName = $"{r.Name}-sa";
                    var identityClientId = appIdentity.IdentityResource.ClientId;

                    // Use KubernetesServiceCustomizationAnnotation to inject SA + pod spec changes
                    // during Helm chart generation.
                    r.Annotations.Add(new KubernetesServiceCustomizationAnnotation(kubeResource =>
                    {
                        // Create ServiceAccount with workload identity annotations
                        var serviceAccount = new ServiceAccountV1();
                        serviceAccount.Metadata.Name = saName;
                        serviceAccount.Metadata.Annotations["azure.workload.identity/client-id"] =
                            $"{{{{ .Values.parameters.{r.Name}.identityClientId }}}}";
                        serviceAccount.Metadata.Labels["azure.workload.identity/use"] = "true";
                        kubeResource.AdditionalResources.Add(serviceAccount);

                        // Add a placeholder parameter for the identity clientId
                        // so it appears in values.yaml under parameters.<name>.identityClientId.
                        // The actual value is resolved at deploy time via CapturedHelmValueProviders.
                        kubeResource.Parameters["identityClientId"] = new KubernetesResource.HelmValue(
                            $"{{{{ .Values.parameters.{r.Name}.identityClientId }}}}",
                            string.Empty);

                        // Set serviceAccountName on pod spec and add workload identity label
                        if (kubeResource.Workload?.PodTemplate is { } podTemplate)
                        {
                            if (podTemplate.Spec is { } podSpec)
                            {
                                podSpec.ServiceAccountName = saName;
                            }

                            // The workload identity webhook requires this label on the POD
                            // to inject AZURE_CLIENT_ID, token volume mounts, etc.
                            podTemplate.Metadata.Labels["azure.workload.identity/use"] = "true";
                        }
                    }));

                    // Wire the identity clientId as a deferred Helm value so it gets
                    // resolved from the Bicep output at deploy time. The SA annotation
                    // references {{ .Values.parameters.<name>.identityClientId }}.
                    if (identityClientId is IValueProvider clientIdProvider)
                    {
                        environment.KubernetesEnvironment.CapturedHelmValueProviders.Add(
                            new KubernetesEnvironmentResource.CapturedHelmValueProvider(
                                "parameters",
                                r.Name,
                                "identityClientId",
                                clientIdProvider));
                    }

                    // Store the identity reference for federated credential Bicep generation
                    environment.WorkloadIdentities[r.Name] = appIdentity.IdentityResource;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Ensures the AKS environment has at least one user node pool. If none exists,
    /// creates a default "workload" user pool and adds it to the app model.
    /// </summary>
    private static AksNodePoolResource? EnsureDefaultUserNodePool(
        AzureKubernetesEnvironmentResource environment,
        DistributedApplicationModel appModel)
    {
        var hasUserPool = environment.NodePools.Any(p => p.Mode is AksNodePoolMode.User);

        if (hasUserPool)
        {
            // Return the first user pool. Search the app model for the existing
            // AksNodePoolResource so we use the same object identity as AddNodePool created.
            var firstUserConfig = environment.NodePools.First(p => p.Mode is AksNodePoolMode.User);
            return FindNodePoolResource(appModel, environment, firstUserConfig.Name);
        }

        // No user pool configured — create a default one and add it to the app model.
        var defaultConfig = new AksNodePoolConfig("workload", "Standard_D2s_v5", 1, 3, AksNodePoolMode.User);
        environment.NodePools.Add(defaultConfig);

        var defaultPool = new AksNodePoolResource("workload", defaultConfig, environment);
        defaultPool.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        appModel.Resources.Add(defaultPool);
        return defaultPool;
    }

    /// <summary>
    /// Finds an existing AksNodePoolResource in the app model by name,
    /// or creates one if not found (for pools added via config but not via AddNodePool).
    /// </summary>
    private static AksNodePoolResource FindNodePoolResource(
        DistributedApplicationModel appModel,
        AzureKubernetesEnvironmentResource environment,
        string poolName)
    {
        // Search the app model for an existing pool resource with matching name and parent
        var existing = appModel.Resources
            .OfType<AksNodePoolResource>()
            .FirstOrDefault(p => p.Name == poolName && p.AksParent == environment);

        if (existing is not null)
        {
            return existing;
        }

        // Pool was added via NodePools config but not via AddNodePool — create the resource
        var config = environment.NodePools.First(p => p.Name == poolName);
        var pool = new AksNodePoolResource(poolName, config, environment);
        pool.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        appModel.Resources.Add(pool);
        return pool;
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
                // Get the actual provisioned cluster name from the Bicep output.
                // The Azure.Provisioning SDK may add a unique suffix to the name
                // (e.g., take('aks-${uniqueString(resourceGroup().id)}', 63)).
                var clusterName = await environment.NameOutputReference.GetValueAsync(context.CancellationToken).ConfigureAwait(false)
                    ?? environment.Name;

                var azPath = FindAzCli();

                // Defense-in-depth: validate that values used as CLI arguments
                // contain only expected characters (alphanumeric, hyphens, underscores, dots).
                ValidateAzureResourceName(clusterName, "cluster name");

                var resourceGroup = await GetResourceGroupAsync(azPath, clusterName, context)
                    .ConfigureAwait(false);

                ValidateAzureResourceName(resourceGroup, "resource group");

                // Fetch kubeconfig content to stdout using --file - to avoid az CLI
                // writing credentials with potentially permissive file permissions.
                // We then write the content ourselves to a temp file with controlled access.
                var fileSystemService = context.Services.GetRequiredService<IFileSystemService>();
                var kubeConfigDir = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-aks");
                var kubeConfigPath = Path.Combine(kubeConfigDir.Path, "kubeconfig");

                context.Logger.LogInformation(
                    "Fetching AKS credentials: cluster={ClusterName}, resourceGroup={ResourceGroup}",
                    clusterName, resourceGroup);

                var result = await RunAzCommandAsync(
                    azPath,
                    $"aks get-credentials --resource-group \"{resourceGroup}\" --name \"{clusterName}\" --file -",
                    context.Logger).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"az aks get-credentials failed (exit code {result.ExitCode}): {result.StandardError}");
                }

                // Write kubeconfig content to a temp file we control.
                // The IFileSystemService temp directory is auto-cleaned on dispose.
                await File.WriteAllTextAsync(kubeConfigPath, result.StandardOutput, context.CancellationToken).ConfigureAwait(false);

                // On Unix, restrict file permissions to owner-only (0600)
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(kubeConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        var azPath = PathLookupHelper.FindFullPathFromPath("az");
        if (azPath is null)
        {
            throw new InvalidOperationException(
                "Azure CLI (az) not found. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli");
        }
        return azPath;
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

        var result = await RunAzCommandAsync(
            azPath,
            $"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"{clusterName}\" --query [0].resourceGroup -o tsv",
            context.Logger).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az resource list failed (exit code {result.ExitCode}): {result.StandardError}");
        }

        resourceGroup = result.StandardOutput.Trim().ReplaceLineEndings("").Trim();

        if (string.IsNullOrEmpty(resourceGroup))
        {
            throw new InvalidOperationException(
                $"Could not resolve resource group for AKS cluster '{clusterName}'. " +
                "Ensure Azure provisioning has completed.");
        }

        return resourceGroup;
    }

    /// <summary>
    /// Runs an az CLI command using the shared ProcessSpec/ProcessUtil infrastructure.
    /// Returns the captured stdout, stderr, and exit code.
    /// </summary>
    private static async Task<AzCommandResult> RunAzCommandAsync(
        string azPath,
        string arguments,
        ILogger logger)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var spec = new ProcessSpec(azPath)
        {
            Arguments = arguments,
            OnOutputData = data => stdout.AppendLine(data),
            OnErrorData = data => stderr.AppendLine(data),
            ThrowOnNonZeroReturnCode = false
        };

        logger.LogDebug("Running: {AzPath} {Arguments}", azPath, arguments);

        var (task, disposable) = ProcessUtil.Run(spec);

        try
        {
            var result = await task.ConfigureAwait(false);
            return new AzCommandResult(result.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record AzCommandResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Validates that an Azure resource name contains only expected characters.
    /// Azure resource names and resource group names allow alphanumeric, hyphens,
    /// underscores, parentheses, and dots.
    /// </summary>
    private static void ValidateAzureResourceName(string value, string parameterDescription)
    {
        if (!AzureResourceNamePattern().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"The {parameterDescription} '{value}' contains unexpected characters. " +
                $"Expected only alphanumeric characters, hyphens, underscores, parentheses, and dots.");
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_\.\(\)]+$")]
    private static partial Regex AzureResourceNamePattern();
}
