// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline step types used for push/deploy dependency wiring
#pragma warning disable ASPIREPIPELINES002 // IDeploymentStateManager is experimental
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource.ProvisionInfrastructureStepName for pipeline ordering
#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService/TempDirectory are experimental

using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// AKS-specific pipeline step implementations for <see cref="AzureKubernetesEnvironmentResource"/>.
/// </summary>
public partial class AzureKubernetesEnvironmentResource
{
    // Test seams. These let tests execute the registered aks-get-credentials-{name} pipeline
    // step end-to-end without the Azure CLI on PATH and without spawning a process, so the
    // assertions cover the call site rather than only the helpers it delegates to. Asserting
    // on the helpers alone would let the original wrong-subscription bug be reintroduced here
    // undetected, which is precisely how #19216 shipped.
    internal Func<string>? AzCliPathResolverForTesting { get; set; }
    internal Func<string, string, ILogger, Task<AzCommandResult>>? AzCommandRunnerForTesting { get; set; }

    /// <summary>
    /// Per-environment AKS preparation work invoked by the <c>prepare-aks-{name}</c> pipeline
    /// step. Ensures a default user node pool exists and applies node-pool affinity and
    /// workload-identity annotations to compute resources targeting this AKS environment so
    /// that the inner Kubernetes environment can consume them when materializing service
    /// resources.
    /// </summary>
    private Task PrepareAksEnvironmentAsync(PipelineStepContext context)
    {
        var appModel = context.Model;
        var executionContext = context.ExecutionContext;

        if (executionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        var logger = context.Services.GetRequiredService<ILogger<AzureKubernetesEnvironmentResource>>();

        logger.LogInformation("Processing AKS environment '{Name}'", Name);

        // Ensure a default user node pool exists for workload scheduling.
        // The system pool should only run system pods; application workloads
        // need a user pool.
        var defaultUserPool = EnsureDefaultUserNodePool(this, appModel);

        foreach (var r in appModel.GetComputeResources())
        {
            var resourceComputeEnvironment = r.GetComputeEnvironment();

            // Check if this resource targets THIS AKS environment
            if (resourceComputeEnvironment is not null && resourceComputeEnvironment != this)
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
                OidcIssuerEnabled = true;
                WorkloadIdentityEnabled = true;

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
                    KubernetesEnvironment.CapturedHelmValueProviders.Add(
                        new KubernetesEnvironmentResource.CapturedHelmValueProvider(
                            "parameters",
                            r.Name,
                            "identityClientId",
                            clientIdProvider));
                }

                // Store the identity reference for federated credential Bicep generation
                WorkloadIdentities[r.Name] = appIdentity.IdentityResource;
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

        if (appModel.Resources.TryGetByName("workload", out var existingResource) && existingResource is AksNodePoolResource existingPool)
        {
            return existingPool;
        }

        var defaultPool = new AksNodePoolResource("workload", defaultConfig, environment);
        defaultPool.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        defaultPool.Annotations.Add(new ResourceIconAnnotation("Cpu"));
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
        pool.Annotations.Add(new ResourceIconAnnotation("Cpu"));
        appModel.Resources.Add(pool);
        return pool;
    }

    /// <summary>
    /// Fetches AKS credentials into an isolated kubeconfig file using az aks get-credentials,
    /// then sets the KubeConfigPath on the inner KubernetesEnvironmentResource so that
    /// subsequent Helm and kubectl commands target the AKS cluster.
    /// </summary>
    private async Task GetAksCredentialsAsync(PipelineStepContext context)
    {
        var getCredsTask = await context.ReportingStep.CreateTaskAsync(
            $"Fetching AKS credentials for {Name}",
            context.CancellationToken).ConfigureAwait(false);

        await using (getCredsTask.ConfigureAwait(false))
        {
            try
            {
                // Get the actual provisioned cluster name from the Bicep output.
                // The Azure.Provisioning SDK may add a unique suffix to the name
                // (e.g., take('aks-${uniqueString(resourceGroup().id)}', 63)).
                var clusterName = await NameOutputReference.GetValueAsync(context.CancellationToken).ConfigureAwait(false)
                    ?? Name;

                var azPath = (AzCliPathResolverForTesting ?? FindAzCli)();

                // Defense-in-depth: validate that values used as CLI arguments
                // contain only expected characters (alphanumeric, hyphens, underscores, dots).
                ValidateAzureResourceName(clusterName, "cluster name");

                // Resolve the scope this cluster actually lives in before touching the CLI. A cluster
                // adopted via AsExistingInResourceGroup(...) can sit outside the app's own
                // subscription/resource group, and the provisioner already targets that scope.
                var (scopedSubscription, scopedResourceGroup) = GetExplicitScopeValues();

                var (subscriptionId, savedResourceGroup) = await ResolveDeploymentScopeAsync(
                    scopedSubscription,
                    scopedResourceGroup,
                    context.Services,
                    context.CancellationToken).ConfigureAwait(false);

                ValidateAzureResourceName(subscriptionId, "subscription ID");

                Task<AzCommandResult> RunAzAsync(string path, string arguments)
                    => (AzCommandRunnerForTesting ?? RunAzCommandAsync)(path, arguments, context.Logger);

                var resourceGroup = await GetResourceGroupAsync(
                    azPath,
                    clusterName,
                    subscriptionId,
                    savedResourceGroup,
                    context.Logger,
                    RunAzAsync)
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

                var kubeConfigContent = await FetchKubeConfigAsync(
                    azPath,
                    subscriptionId,
                    resourceGroup,
                    clusterName,
                    RunAzAsync).ConfigureAwait(false);

                // Write kubeconfig content to a temp file we control.
                // The IFileSystemService temp directory is auto-cleaned on dispose.
                await File.WriteAllTextAsync(kubeConfigPath, kubeConfigContent, context.CancellationToken).ConfigureAwait(false);

                // On Unix, restrict file permissions to owner-only (0600)
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(kubeConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                // Set the kubeconfig path on the inner K8s environment so
                // Helm and kubectl commands use --kubeconfig to target this cluster
                KubernetesEnvironment.KubeConfigPath = kubeConfigPath;

                context.Logger.LogInformation(
                    "AKS credentials written to {KubeConfigPath}", kubeConfigPath);

                // Add AKS connection info to the pipeline summary
                context.Summary.Add(
                    "☸ AKS Cluster",
                    new MarkdownString($"**{clusterName}** in resource group **{resourceGroup}**"));

                // Quote the values: ValidateAzureResourceName permits parentheses in resource group
                // names, and an unquoted `team(prod)` is a syntax error in bash and zsh. This hint is
                // advertised as copy-pasteable, so it has to survive the same names the real
                // invocation built by BuildGetCredentialsArguments already handles.
                context.Summary.Add(
                    "🔑 Connect to cluster",
                    new MarkdownString(
                        $"`az aks get-credentials --resource-group '{resourceGroup}' --name '{clusterName}' --subscription {subscriptionId}`"));

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

    /// <summary>
    /// Applies the AGC <c>ApplicationLoadBalancer</c> custom resource for the supplied
    /// <see cref="AzureKubernetesLoadBalancerResource"/> into the cluster. Polls the
    /// cluster for the <c>azure-alb-external</c> GatewayClass first (it appears once the
    /// AGC ALB controller add-on is fully installed), then <c>kubectl apply</c>s the CR
    /// pointing at the load balancer's delegated subnet.
    /// </summary>
    /// <remarks>
    /// The CR shape is documented at
    /// https://learn.microsoft.com/azure/application-gateway/for-containers/quickstart-deploy-application-gateway-for-containers.
    /// Example:
    /// <code>
    /// apiVersion: alb.networking.azure.io/v1
    /// kind: ApplicationLoadBalancer
    /// metadata:
    ///   name: alb-{lb.Name}
    ///   namespace: default
    /// spec:
    ///   associations:
    ///   - /subscriptions/.../subnets/{albSubnet}
    /// </code>
    /// </remarks>
    internal async Task ApplyAlbCrdAsync(
        AzureKubernetesLoadBalancerResource lb,
        PipelineStepContext context)
    {
        var applyTask = await context.ReportingStep.CreateTaskAsync(
            $"Applying AGC ApplicationLoadBalancer CR for {lb.Name}",
            context.CancellationToken).ConfigureAwait(false);

        await using (applyTask.ConfigureAwait(false))
        {
            try
            {
                if (lb.DisplacedDelegationServiceName is { } displaced)
                {
                    // The subnet had an explicit non-trafficControllers service delegation when
                    // AddLoadBalancer was called. AzureSubnetResource emits only the LAST
                    // AzureSubnetServiceDelegationAnnotation, so AGC's trafficControllers
                    // delegation displaced the user's. Warn at deploy time so the user can
                    // either remove the original delegation or use a separate subnet.
                    context.Logger.LogWarning(
                        "AddLoadBalancer overrode an existing service delegation '{DisplacedServiceName}' " +
                        "on the subnet for AGC load balancer '{LoadBalancerName}' with " +
                        "'Microsoft.ServiceNetworking/trafficControllers'. AGC requires this delegation; " +
                        "if you need '{DisplacedServiceName}' to remain, use a separate subnet for the load balancer.",
                        displaced, lb.Name, displaced);
                }

                var subnetId = await ((IValueProvider)lb.SubnetIdReference).GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(subnetId))
                {
                    throw new InvalidOperationException(
                        $"Could not resolve subnet ID for AGC load balancer '{lb.Name}'.");
                }

                var kubeConfigPath = KubernetesEnvironment.KubeConfigPath
                    ?? throw new InvalidOperationException(
                        $"Cannot apply AGC ApplicationLoadBalancer CR for '{lb.Name}': " +
                        $"kubeconfig was not set by aks-get-credentials-{Name}.");

                // Wait for the azure-alb-external GatewayClass to appear. The AGC ALB
                // controller add-on installs it asynchronously, so polling is required
                // even after the AKS cluster reports Succeeded. 10-minute budget matches
                // the E2E test budget in KubernetesGatewayTlsDeploymentTests.cs.
                await WaitForAzureAlbGatewayClassAsync(
                    kubeConfigPath, context.Logger, TimeSpan.FromMinutes(10),
                    context.CancellationToken).ConfigureAwait(false);

                // Apply the ApplicationLoadBalancer CR via kubectl apply -f - using stdin
                // so we don't need a temp file. JSON is a valid YAML subset for kubectl.
                var manifest =
                    $$"""
                    {
                      "apiVersion": "alb.networking.azure.io/v1",
                      "kind": "ApplicationLoadBalancer",
                      "metadata": { "name": "{{lb.AlbName}}", "namespace": "{{AzureKubernetesLoadBalancerResource.AlbNamespace}}" },
                      "spec": { "associations": ["{{subnetId}}"] }
                    }
                    """;

                var applyArgs = $"apply --kubeconfig \"{kubeConfigPath}\" -n \"{AzureKubernetesLoadBalancerResource.AlbNamespace}\" -f -";

                // Buffer stderr (and the tail of stdout, since kubectl sometimes writes
                // structured errors to stdout when --output is not requested) so we can
                // surface the real failure cause in the thrown exception. Without this,
                // the only signal a caller gets is the exit code, which makes RBAC,
                // missing-CRD, and admission-webhook errors very hard to diagnose. Cap
                // the buffer to keep a pathological controller from blowing up the
                // exception message; 4 KB is plenty for the multi-line "the server
                // could not find the requested resource" / "forbidden" / validation
                // messages kubectl emits.
                const int kubectlErrorCaptureBytes = 4 * 1024;
                var errorCapture = new StringBuilder();

                void CaptureLine(string line)
                {
                    if (string.IsNullOrEmpty(line) || errorCapture.Length >= kubectlErrorCaptureBytes)
                    {
                        return;
                    }

                    var remaining = kubectlErrorCaptureBytes - errorCapture.Length;
                    if (line.Length + 1 > remaining)
                    {
                        errorCapture.Append(line, 0, Math.Max(0, remaining - 1));
                    }
                    else
                    {
                        errorCapture.AppendLine(line);
                    }
                }

                var applySpec = new ProcessSpec("kubectl")
                {
                    Arguments = applyArgs,
                    StandardInputContent = manifest,
                    InheritEnv = true,
                    ThrowOnNonZeroReturnCode = false,
                    OnOutputData = line =>
                    {
                        context.Logger.LogDebug("kubectl: {Line}", line);
                        CaptureLine(line);
                    },
                    OnErrorData = line =>
                    {
                        context.Logger.LogDebug("kubectl: {Line}", line);
                        CaptureLine(line);
                    }
                };

                var (applyResultTask, applyDisposable) = ProcessUtil.Run(applySpec);
                int applyExitCode;
                await using (applyDisposable.ConfigureAwait(false))
                {
                    var result = await applyResultTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    applyExitCode = result.ExitCode;
                }

                if (applyExitCode != 0)
                {
                    var capturedOutput = errorCapture.ToString().TrimEnd();
                    var detail = string.IsNullOrEmpty(capturedOutput)
                        ? "kubectl produced no diagnostic output; re-run the deploy with debug logging enabled to see kubectl's stderr."
                        : capturedOutput;

                    throw new InvalidOperationException(
                        $"kubectl apply for ApplicationLoadBalancer '{lb.AlbName}' failed (exit code {applyExitCode}).{Environment.NewLine}{detail}");
                }

                context.Logger.LogInformation(
                    "Applied ApplicationLoadBalancer '{AlbName}' in namespace '{AlbNamespace}' bound to subnet '{SubnetId}'",
                    lb.AlbName, AzureKubernetesLoadBalancerResource.AlbNamespace, subnetId);

                context.Summary.Add(
                    $"☸ ALB {lb.Name}",
                    new MarkdownString($"**{lb.AlbName}** in `{AzureKubernetesLoadBalancerResource.AlbNamespace}` (subnet `{subnetId}`)"));

                await applyTask.SucceedAsync(
                    $"AGC ApplicationLoadBalancer '{lb.AlbName}' applied",
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await applyTask.FailAsync(
                    $"Failed to apply AGC ApplicationLoadBalancer for {lb.Name}: {ex.Message}",
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Polls <c>kubectl get gatewayclass azure-alb-external</c> until it succeeds or the
    /// timeout elapses. The <c>azure-alb-external</c> GatewayClass is installed by the
    /// AGC ALB controller add-on (<c>ingressProfile.applicationLoadBalancer.enabled</c>),
    /// but provisioning is asynchronous — the AKS resource may report Succeeded before the
    /// add-on's CRDs land in the cluster.
    /// </summary>
    private static async Task WaitForAzureAlbGatewayClassAsync(
        string kubeConfigPath,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(10);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            attempt++;
            // Silent OnErrorData/OnOutputData is intentional for this poll loop:
            // every probe before the GatewayClass lands prints a "NotFound" stderr line
            // that would just be noise. The terminal failure mode (timeout) below
            // emits an actionable error that points at the AKS preview feature flags.
            var (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = $"get gatewayclass azure-alb-external --kubeconfig \"{kubeConfigPath}\" --no-headers",
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = _ => { },
                OnErrorData = _ => { }
            });

            int exitCode;
            await using (disposable.ConfigureAwait(false))
            {
                var result = await resultTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = result.ExitCode;
            }

            if (exitCode == 0)
            {
                logger.LogInformation(
                    "GatewayClass 'azure-alb-external' is available (after {Attempts} probe(s))",
                    attempt);
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "Timed out waiting for the 'azure-alb-external' GatewayClass to appear in the cluster. " +
                    "Ensure the AKS preview features 'Microsoft.ContainerService/AKSGatewayAPIPreview' and " +
                    "'Microsoft.ContainerService/AKSAppGatewayContainersPreview' are registered on the subscription, " +
                    "and that the AGC ALB controller add-on (ingressProfile.applicationLoadBalancer) finished installing.");
            }

            logger.LogDebug(
                "GatewayClass 'azure-alb-external' not yet available (attempt {Attempt}); retrying in {Delay}s",
                attempt, pollInterval.TotalSeconds);

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
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
    /// Gets the subscription and resource group this resource explicitly targets, if any.
    /// </summary>
    /// <remarks>
    /// Mirrors the precedence in <c>BicepUtilities.GetExistingResourceScope</c>: an explicitly
    /// assigned <see cref="AzureBicepResource.Scope"/> (which <c>ConfigureInfrastructure</c> can set)
    /// wins over the <see cref="ExistingAzureResourceAnnotation"/> that <c>AsExistingInResourceGroup</c>
    /// and friends attach. The credential fetch has to agree with whatever the provisioner deployed
    /// against. Values are returned unresolved because they may be a literal string, a
    /// <see cref="ParameterResource"/>, or a <see cref="BicepOutputReference"/>.
    /// </remarks>
    private (object? Subscription, object? ResourceGroup) GetExplicitScopeValues()
    {
        if (Scope is not null)
        {
            // A tenant-scoped resource pins neither value. HasResourceGroup must be checked first
            // because the ResourceGroup getter throws for subscription- and tenant-scoped resources.
            return Scope.IsTenantScope
                ? (null, null)
                : (Scope.Subscription, Scope.HasResourceGroup ? Scope.ResourceGroup : null);
        }

        if (this.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existing) && !existing.IsTenantScope)
        {
            return (existing.Subscription, existing.ResourceGroup);
        }

        return (null, null);
    }

    /// <summary>
    /// Reads the global Azure deployment state without requiring a subscription to be present.
    /// </summary>
    internal static async Task<(string? SubscriptionId, string? ResourceGroup)> TryGetAzureDeploymentStateAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var azureState = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        // Use ToString() rather than GetValue<string>() to match how AzureEnvironmentResource reads
        // these same keys, and because GetValue<string>() throws if hand-edited state stores a
        // non-string JSON value.
        return (azureState.Data["SubscriptionId"]?.ToString(), azureState.Data["ResourceGroup"]?.ToString());
    }

    /// <summary>
    /// Resolves a scope value that may be a literal string or a deferred value such as a
    /// <see cref="ParameterResource"/> or a <see cref="BicepOutputReference"/>.
    /// </summary>
    /// <remarks>
    /// Matches <c>BicepProvisioner.ResolveScopeValueAsync</c>, including its refusal to accept a
    /// null result from a provider. Falling back to the app's own subscription in that case would
    /// be worse than failing: provisioning would have thrown, while the credential fetch would
    /// quietly target the wrong scope and could adopt a same-named cluster there. Empty is rejected
    /// for the same reason, since the string.IsNullOrEmpty checks downstream would treat it as
    /// unpinned. Nothing upstream rejects empty (the scope constructors and
    /// <c>AsExistingInResourceGroup</c> only guard against null), so a literal is checked too.
    /// </remarks>
    internal static async Task<string?> ResolveScopeValueAsync(object? value, CancellationToken cancellationToken)
        => value switch
        {
            null => null,
            string { Length: > 0 } s => s,
            IValueProvider provider when
                await provider.GetValueAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } resolved => resolved,
            string or IValueProvider => throw new InvalidOperationException(
                "The Azure resource scope value cannot be null or empty."),
            _ => throw new NotSupportedException(
                $"The Azure scope value type {value.GetType()} is not supported.")
        };

    /// <summary>
    /// Resolves the subscription and resource group that this AKS cluster actually lives in.
    /// </summary>
    /// <remarks>
    /// A cluster adopted with <c>AsExistingInResourceGroup(...)</c> can sit in a different
    /// subscription and resource group than the one Aspire deploys the rest of the app into, and the
    /// provisioner targets that per-resource scope. The Azure CLI calls here have to agree with it,
    /// otherwise we would authenticate against the wrong subscription and could even find a
    /// same-named cluster in the wrong place. Values the resource does not pin fall back to the
    /// global deployment state.
    /// </remarks>
    internal static async Task<(string SubscriptionId, string? ResourceGroup)> ResolveDeploymentScopeAsync(
        object? scopedSubscription,
        object? scopedResourceGroup,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var subscriptionId = await ResolveScopeValueAsync(scopedSubscription, cancellationToken).ConfigureAwait(false);
        var resourceGroup = await ResolveScopeValueAsync(scopedResourceGroup, cancellationToken).ConfigureAwait(false);

        // Fully pinned by the resource, so the global deployment state is irrelevant and must not be
        // required. This matters because the app's own subscription may legitimately be absent when
        // every Azure resource is an adopted existing one.
        if (!string.IsNullOrEmpty(subscriptionId) && !string.IsNullOrEmpty(resourceGroup))
        {
            return (subscriptionId, resourceGroup);
        }

        var (globalSubscriptionId, globalResourceGroup) =
            await TryGetAzureDeploymentStateAsync(services, cancellationToken).ConfigureAwait(false);

        var pinnedSubscription = !string.IsNullOrEmpty(subscriptionId);
        subscriptionId = pinnedSubscription ? subscriptionId : globalSubscriptionId;

        if (string.IsNullOrEmpty(subscriptionId))
        {
            throw new InvalidOperationException(
                "Could not resolve the Azure subscription selected for deployment. " +
                "Ensure Azure provisioning has completed, or set the Azure:SubscriptionId configuration value.");
        }

        if (string.IsNullOrEmpty(resourceGroup))
        {
            // The saved resource group only names a group inside the saved subscription. Inheriting it
            // across a subscription boundary would point at a group that may not exist there, or worse
            // at an unrelated group that happens to share the name, so force discovery instead.
            resourceGroup = pinnedSubscription && !string.Equals(subscriptionId, globalSubscriptionId, StringComparison.OrdinalIgnoreCase)
                ? null
                : globalResourceGroup;
        }

        return (subscriptionId, resourceGroup);
    }

    /// <summary>
    /// Gets the resource group the cluster lives in, preferring an already-resolved one and falling
    /// back to an Azure CLI query.
    /// </summary>
    /// <remarks>
    /// <paramref name="savedResourceGroup"/> is the resource group from the resolved deployment
    /// scope, which may have come from <c>AzureBicepResource.Scope</c> or an
    /// <c>ExistingAzureResourceAnnotation</c> rather than from deployment state. It is null when the
    /// resource group is genuinely unknown: either deployment state predates it being recorded, or
    /// <see cref="ResolveDeploymentScopeAsync"/> deliberately dropped it because the resource pins a
    /// different subscription than the app deploys into, where the saved name would be meaningless
    /// or, worse, match an unrelated group.
    /// <para>
    /// <paramref name="runAzCommandAsync"/> is injected so tests can verify that the Azure CLI
    /// fallback is scoped to the resolved subscription without invoking the real az CLI.
    /// </para>
    /// </remarks>
    internal static async Task<string> GetResourceGroupAsync(
        string azPath,
        string clusterName,
        string subscriptionId,
        string? savedResourceGroup,
        ILogger logger,
        Func<string, string, Task<AzCommandResult>> runAzCommandAsync)
    {
        if (!string.IsNullOrEmpty(savedResourceGroup))
        {
            return savedResourceGroup;
        }

        // Keep the query scoped to the resolved subscription rather than the CLI default, otherwise
        // a same-named cluster in the ambient subscription could be picked up instead.
        logger.LogDebug(
            "Resource group not in deployment state, querying Azure for cluster '{ClusterName}'",
            clusterName);

        var result = await runAzCommandAsync(
            azPath,
            BuildResourceGroupQueryArguments(subscriptionId, clusterName)).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az resource list failed (exit code {result.ExitCode}): {result.StandardError}");
        }

        // With '-o tsv' the query emits one resource group per matching cluster, newline separated:
        //   my-rg
        //   other-rg
        // A cluster name is only unique within a resource group, not within a subscription, so the
        // query can legitimately return several rows. Picking one would silently deploy into, and
        // hand back credentials for, an unrelated cluster.
        var resourceGroups = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (resourceGroups.Length == 0)
        {
            throw new InvalidOperationException(
                $"Could not resolve resource group for AKS cluster '{clusterName}'. " +
                "Ensure Azure provisioning has completed.");
        }

        if (resourceGroups.Length > 1)
        {
            throw new InvalidOperationException(
                $"Found {resourceGroups.Length} AKS clusters named '{clusterName}' in subscription " +
                $"'{subscriptionId}' (resource groups: {string.Join(", ", resourceGroups)}). " +
                "Specify which one to use by calling AsExistingInResourceGroup on the resource.");
        }

        return resourceGroups[0];
    }

    /// <summary>
    /// Fetches the kubeconfig content for the cluster from the Azure CLI.
    /// </summary>
    /// <remarks>
    /// <paramref name="runAzCommandAsync"/> is injected so tests can verify that the credential
    /// fetch is scoped to the deployment subscription without invoking the real az CLI.
    /// </remarks>
    internal static async Task<string> FetchKubeConfigAsync(
        string azPath,
        string subscriptionId,
        string resourceGroup,
        string clusterName,
        Func<string, string, Task<AzCommandResult>> runAzCommandAsync)
    {
        var result = await runAzCommandAsync(
            azPath,
            BuildGetCredentialsArguments(subscriptionId, resourceGroup, clusterName)).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"az aks get-credentials failed (exit code {result.ExitCode}): {result.StandardError}");
        }

        return result.StandardOutput;
    }

    internal static string BuildGetCredentialsArguments(
        string subscriptionId,
        string resourceGroup,
        string clusterName)
        => $"aks get-credentials --resource-group \"{resourceGroup}\" --name \"{clusterName}\" --file - --subscription \"{subscriptionId}\"";

    internal static string BuildResourceGroupQueryArguments(string subscriptionId, string clusterName)
        => $"resource list --resource-type Microsoft.ContainerService/managedClusters --name \"{clusterName}\" --query [].resourceGroup -o tsv --subscription \"{subscriptionId}\"";

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

    internal sealed record AzCommandResult(int ExitCode, string StandardOutput, string StandardError);

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
