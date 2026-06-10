// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes environment resource that can host application resources.
/// </summary>
/// <remarks>
/// This resource models the Kubernetes publishing environment used by Aspire when generating
/// Helm charts and other Kubernetes manifests for application resources that will run on a
/// Kubernetes cluster.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class KubernetesEnvironmentResource : Resource, IComputeEnvironmentResource
{
    /// <summary>
    /// Gets or sets the name of the Helm chart to be generated.
    /// </summary>
    /// <remarks>
    /// Defaults to the application name. Use
    /// <see cref="HelmChartOptions.WithChartName(string)"/> via
    /// <see cref="KubernetesEnvironmentExtensions.WithHelm(IResourceBuilder{KubernetesEnvironmentResource}, Action{HelmChartOptions})"/>
    /// to customize the chart name.
    /// </remarks>
    internal string HelmChartName { get; set; } = "aspire";

    /// <summary>
    /// Gets or sets the version of the Helm chart to be generated.
    /// This property specifies the version number that will be assigned to the Helm chart,
    /// typically following semantic versioning conventions.
    /// </summary>
    /// <remarks>
    /// Use <see cref="HelmChartOptions.WithChartVersion(string)"/> via
    /// <see cref="KubernetesEnvironmentExtensions.WithHelm(IResourceBuilder{KubernetesEnvironmentResource}, Action{HelmChartOptions})"/>
    /// to customize the chart version.
    /// </remarks>
    internal string HelmChartVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Gets or sets the description of the Helm chart being generated.
    /// </summary>
    /// <remarks>
    /// Use <see cref="HelmChartOptions.WithChartDescription(string)"/> via
    /// <see cref="KubernetesEnvironmentExtensions.WithHelm(IResourceBuilder{KubernetesEnvironmentResource}, Action{HelmChartOptions})"/>
    /// to customize the chart description.
    /// </remarks>
    internal string HelmChartDescription { get; set; } = "Aspire Helm Chart";

    /// <summary>
    /// Determines whether to include an Aspire dashboard for telemetry visualization in this environment.
    /// </summary>
    public bool DashboardEnabled { get; set; } = true;

    internal IResourceBuilder<KubernetesAspireDashboardResource>? Dashboard { get; set; }

    /// <summary>
    /// Specifies the default type of storage used for Kubernetes deployments.
    /// </summary>
    /// <remarks>
    /// This property determines the storage medium used for the application.
    /// Possible values include "emptyDir", "hostPath", "pvc"
    /// </remarks>
    public string DefaultStorageType { get; set; } = "emptyDir";

    /// <summary>
    /// Specifies the default name of the storage class to be used for persistent volume claims in Kubernetes.
    /// This property allows customization of the storage class for specifying storage requirements
    /// such as performance, retention policies, and provisioning parameters.
    /// If set to null, the default storage class for the cluster will be used.
    /// </summary>
    public string? DefaultStorageClassName { get; set; }

    /// <summary>
    /// Gets or sets the default storage size for persistent volumes.
    /// </summary>
    public string DefaultStorageSize { get; set; } = "1Gi";

    /// <summary>
    /// Gets or sets the default access policy for reading and writing to the storage.
    /// </summary>
    public string DefaultStorageReadWritePolicy { get; set; } = "ReadWriteOnce";

    /// <summary>
    /// Gets or sets the default policy that determines how Docker images are pulled during deployment.
    /// Possible values are:
    /// "Always" - Always attempt to pull the image from the registry.
    /// "IfNotPresent" - Pull the image only if it is not already present locally.
    /// "Never" - Never pull the image, use only the local image.
    /// The default value is "IfNotPresent".
    /// </summary>
    public string DefaultImagePullPolicy { get; set; } = "IfNotPresent";

    /// <summary>
    /// Gets or sets the default Kubernetes service type to be used when generating artifacts.
    /// </summary>
    /// <remarks>
    /// The default value is "ClusterIP". This property determines the type of service
    /// (e.g., ClusterIP, NodePort, LoadBalancer) created in Kubernetes for the application.
    /// </remarks>
    public string DefaultServiceType { get; set; } = "ClusterIP";

    /// <summary>
    /// Gets or sets the path to an explicit kubeconfig file for Helm and kubectl commands.
    /// When set, all Helm and kubectl commands will use <c>--kubeconfig</c> to target
    /// this file instead of the default <c>~/.kube/config</c>.
    /// </summary>
    /// <remarks>
    /// This is used by Azure Kubernetes Service (AKS) integration to isolate credentials
    /// fetched via <c>az aks get-credentials</c> from the user's default kubectl context.
    /// </remarks>
    public string? KubeConfigPath { get; set; }

    /// <summary>
    /// Gets or sets the parent compute environment resource that owns this Kubernetes environment.
    /// When set, resources with <c>WithComputeEnvironment</c> targeting the parent will also
    /// be processed by this Kubernetes environment.
    /// </summary>
    /// <remarks>
    /// This is used by Azure Kubernetes Service (AKS) integration where the user calls
    /// <c>WithComputeEnvironment(aksEnv)</c> but the inner <c>KubernetesEnvironmentResource</c>
    /// needs to process the resource.
    /// </remarks>
    public IComputeEnvironmentResource? OwningComputeEnvironment { get; set; }

    internal IPortAllocator PortAllocator { get; } = new PortAllocator();

    /// <summary>
    /// Captured parameter-to-values.yaml mappings populated during publish, consumed during deploy
    /// to resolve secret and unresolved parameter values into the environment values file.
    /// </summary>
    internal List<CapturedHelmValue> CapturedHelmValues { get; } = [];

    /// <summary>
    /// Captured cross-resource secret references populated during publish, consumed during deploy
    /// to resolve composite values that reference other Helm values paths.
    /// </summary>
    internal List<CapturedHelmCrossReference> CapturedHelmCrossReferences { get; } = [];

    /// <summary>
    /// Captured container image references populated during publish, consumed during deploy
    /// to resolve the full registry-prefixed image name (e.g., "myregistry.azurecr.io/server:latest").
    /// </summary>
    internal List<CapturedHelmImageReference> CapturedHelmImageReferences { get; } = [];

    /// <summary>
    /// Represents a captured mapping from a Helm values.yaml path to a <see cref="ParameterResource"/>
    /// for deferred resolution during deploy.
    /// </summary>
    internal sealed record CapturedHelmValue(string Section, string ResourceKey, string ValueKey, ParameterResource Parameter);

    /// <summary>
    /// Represents a captured cross-resource secret reference where the value contains Helm expressions
    /// referencing other values.yaml paths (e.g., connection strings containing <c>{{ .Values.secrets.cache.password }}</c>).
    /// At deploy time, the Helm expressions in the template are substituted with resolved values.
    /// </summary>
    internal sealed record CapturedHelmCrossReference(string Section, string ResourceKey, string ValueKey, string TemplateValue);

    /// <summary>
    /// Represents a captured container image reference that needs deploy-time resolution
    /// to prepend the container registry (e.g., "server:latest" → "myregistry.azurecr.io/server:latest").
    /// </summary>
    internal sealed record CapturedHelmImageReference(string Section, string ResourceKey, string ValueKey, IResource Resource);

    /// <summary>
    /// Represents a captured value provider reference that needs deploy-time resolution.
    /// This handles any <see cref="IValueProvider"/> implementation (e.g., Bicep output references,
    /// connection strings) that can't be resolved at publish time.
    /// </summary>
    internal sealed record CapturedHelmValueProvider(string Section, string ResourceKey, string ValueKey, IValueProvider ValueProvider);

    /// <summary>
    /// Captured value provider references populated during publish, consumed during deploy
    /// to resolve values from external sources (e.g., Azure Bicep outputs).
    /// </summary>
    internal List<CapturedHelmValueProvider> CapturedHelmValueProviders { get; } = [];

    /// <summary>
    /// Gets or sets the delegate that creates deployment pipeline steps for the configured engine.
    /// </summary>
    internal Func<KubernetesEnvironmentResource, PipelineStepFactoryContext, Task<IReadOnlyList<PipelineStep>>>? DeploymentEngineStepsFactory { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="KubernetesEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Kubernetes environment.</param>
    public KubernetesEnvironmentResource(string name) : base(name)
    {
        // Publish step - generates Helm chart YAML artifacts
        Annotations.Add(new PipelineStepAnnotation(async (factoryContext) =>
        {
            var environment = (KubernetesEnvironmentResource)factoryContext.Resource;
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>();

            // Add prepare-deployment-targets-{name} step that materializes Kubernetes
            // service resources and DeploymentTargetAnnotations for compute resources
            // targeted to this environment. Runs before the BeforeStart synchronization
            // point so downstream code can observe the deployment targets.
            var prepareStep = new PipelineStep
            {
                Name = $"prepare-deployment-targets-{Name}",
                Description = $"Prepares Kubernetes deployment targets for {Name}.",
                Action = ctx => PrepareDeploymentTargetsAsync(ctx),
                DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            };

            steps.Add(prepareStep);

            // Publish step
            var publishStep = new PipelineStep
            {
                Name = $"publish-{Name}",
                Description = $"Publishes the Kubernetes environment configuration for {Name}.",
                Action = ctx => PublishAsync(ctx)
            };
            // Depend on publish-prereq so that process-parameters has run before we
            // resolve Helm chart annotations (chart name/version/description) that may
            // be backed by ParameterResource values.
            publishStep.DependsOn(WellKnownPipelineSteps.PublishPrereq);
            publishStep.RequiredBy(WellKnownPipelineSteps.Publish);
            steps.Add(publishStep);

            // Deployment engine steps (e.g., Helm prepare, deploy, uninstall)
            if (environment.DeploymentEngineStepsFactory is not null)
            {
                var engineSteps = await environment.DeploymentEngineStepsFactory(environment, factoryContext).ConfigureAwait(false);
                steps.AddRange(engineSteps);
            }

            // TLS bootstrap step — creates self-signed placeholder secrets after Helm deploy
            // for any Ingress/Gateway with TLS configured, if the secret doesn't already exist.
            var tlsSecrets = CollectTlsSecrets(model, environment);
            if (tlsSecrets.Count > 0)
            {
                var tlsBootstrapStep = new PipelineStep
                {
                    Name = $"tls-bootstrap-{environment.Name}",
                    Description = "Creates self-signed bootstrap TLS secrets if they don't already exist",
                    Action = ctx => BootstrapTlsSecretsAsync(ctx, environment, tlsSecrets)
                };
                tlsBootstrapStep.DependsOn($"helm-deploy-{environment.Name}");
                tlsBootstrapStep.RequiredBy(WellKnownPipelineSteps.Deploy);
                steps.Add(tlsBootstrapStep);
            }

            // FQDN discovery step — for Gateway TLS configs with no hostnames, waits for the
            // Gateway to be assigned an address, patches the listener hostname, and bootstraps TLS.
            var gatewaysNeedingDiscovery = CollectGatewaysNeedingFqdnDiscovery(model, environment);
            if (gatewaysNeedingDiscovery.Count > 0)
            {
                var fqdnDiscoveryStep = new PipelineStep
                {
                    Name = $"tls-fqdn-discovery-{environment.Name}",
                    Description = "Discovers Gateway FQDN, patches listener hostname, and bootstraps TLS",
                    Action = ctx => DiscoverFqdnAndBootstrapTlsAsync(ctx, environment, gatewaysNeedingDiscovery)
                };
                fqdnDiscoveryStep.DependsOn($"helm-deploy-{environment.Name}");
                if (tlsSecrets.Count > 0)
                {
                    // Run after normal TLS bootstrap (which handles hostnames that are already known)
                    fqdnDiscoveryStep.DependsOn($"tls-bootstrap-{environment.Name}");
                }
                fqdnDiscoveryStep.RequiredBy(WellKnownPipelineSteps.Deploy);
                steps.Add(fqdnDiscoveryStep);
            }

            // Pre-helm cleanup step — strip any stale non-helm Update entries from the
            // managedFields of Gateways with TLS. Older Aspire builds patched the listener
            // hostname with kubectl's default field manager ("kubectl-patch"); that Update
            // entry persists in managedFields after later builds switched to
            // --field-manager=helm, and helm's server-side apply still conflicts with the
            // foreign Update ownership of .spec.listeners[name="https"].hostname. Removing
            // the stale entry up front lets helm upgrade succeed without --force-conflicts.
            // No-op on first deploy (the Gateway doesn't exist yet) and on clusters that
            // were only ever managed by current code (no foreign managers found).
            var gatewaysWithTls = CollectGatewaysWithTls(model, environment);
            if (gatewaysWithTls.Count > 0)
            {
                var cleanupStep = new PipelineStep
                {
                    Name = $"gateway-field-cleanup-{environment.Name}",
                    Description = "Removes stale foreign field-manager entries from existing Gateways so helm upgrade can re-apply",
                    Action = ctx => CleanupGatewayFieldOwnershipAsync(ctx, environment, gatewaysWithTls)
                };
                cleanupStep.RequiredBy($"helm-deploy-{environment.Name}");
                cleanupStep.DependsOn($"prepare-{environment.Name}");
                steps.Add(cleanupStep);
            }

            // Expand deployment target steps for compute resources (including dashboard if enabled)
            var resources = environment.DashboardEnabled && environment.Dashboard?.Resource is KubernetesAspireDashboardResource dashboard
                ? [.. model.GetComputeResources(), dashboard]
                : model.GetComputeResources();

            foreach (var computeResource in resources)
            {
                var targetEnv = environment.OwningComputeEnvironment ?? environment;
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(targetEnv)?.DeploymentTarget;
                if (deploymentTarget is not null &&
                    deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
                {
                    foreach (var annotation in annotations)
                    {
                        var childFactoryContext = new PipelineStepFactoryContext
                        {
                            PipelineContext = factoryContext.PipelineContext,
                            Resource = deploymentTarget
                        };

                        var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext).ConfigureAwait(false);

                        foreach (var step in deploymentTargetSteps)
                        {
                            step.Resource ??= deploymentTarget;
                        }

                        steps.AddRange(deploymentTargetSteps);
                    }
                }
            }

            return steps;
        }));

        // Pipeline configuration - wire up step dependencies
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            var resources = DashboardEnabled && Dashboard?.Resource is KubernetesAspireDashboardResource dashboardRes
                ? [.. context.Model.GetComputeResources(), dashboardRes]
                : context.Model.GetComputeResources();

            foreach (var computeResource in resources)
            {
                var targetEnv = OwningComputeEnvironment ?? this;
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(targetEnv)?.DeploymentTarget;
                if (deploymentTarget is null)
                {
                    continue;
                }

                // Build steps must complete before the deploy step
                var buildSteps = context.GetSteps(computeResource, WellKnownPipelineTags.BuildCompute);
                buildSteps.RequiredBy(WellKnownPipelineSteps.Deploy)
                          .DependsOn(WellKnownPipelineSteps.DeployPrereq);

                // Push steps must complete before the helm deploy step
                var pushSteps = context.GetSteps(computeResource, WellKnownPipelineTags.PushContainerImage);
                var helmDeploySteps = context.GetSteps(targetEnv, "helm-deploy");
                helmDeploySteps.DependsOn(pushSteps);

                // Print summary steps are on the deployment target and must run after helm deploy
                var printSummarySteps = context.GetSteps(deploymentTarget, "print-summary");
                printSummarySteps.DependsOn(helmDeploySteps);
            }
        }));
    }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;

        return ReferenceExpression.Create($"{resource.Name.ToServiceName()}");
    }

    private Task PublishAsync(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);

        var kubernetesContext = new KubernetesPublishingContext(
            context.ExecutionContext,
            outputPath,
            context.Logger,
            this,
            context.CancellationToken);
        return kubernetesContext.WriteModelAsync(context.Model, this);
    }

    /// <summary>
    /// Materializes Kubernetes deployment targets for compute resources targeted to this
    /// environment. Invoked by the per-environment <c>prepare-deployment-targets-{name}</c>
    /// pipeline step.
    /// </summary>
    private async Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        var appModel = context.Model;
        var services = context.Services;
        var executionContext = context.ExecutionContext;

        if (executionContext.IsRunMode)
        {
            return;
        }

        var logger = services.GetRequiredService<ILogger<KubernetesEnvironmentResource>>();
        var cancellationToken = context.CancellationToken;

        var environmentContext = new KubernetesEnvironmentContext(this, logger);
        var containerRegistry = GetContainerRegistry(this, appModel);
        var targetComputeEnvironment = OwningComputeEnvironment ?? this;

        // Create a Kubernetes resource for the dashboard if enabled
        if (DashboardEnabled && Dashboard?.Resource is KubernetesAspireDashboardResource dashboard)
        {
            var dashboardService = await environmentContext.CreateKubernetesResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);
            dashboardService.AddPrintSummaryStep();

            dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
            {
                ComputeEnvironment = targetComputeEnvironment,
                ContainerRegistry = containerRegistry
            });
        }

        // Build deployment target lookup for endpoint resolution.
        // Use name-based equality so that endpoint references created through the
        // TypeScript AppHost RPC bridge (which may use a different resource instance)
        // resolve correctly.
        var deploymentTargets = new Dictionary<IResource, KubernetesResource>(new ResourceNameComparer());

        foreach (var r in appModel.GetComputeResources())
        {
            // Skip resources that are explicitly targeted to a different compute environment.
            // Also match if the resource targets a parent compute environment (e.g., AKS)
            // that owns this Kubernetes environment.
            var resourceComputeEnvironment = r.GetComputeEnvironment();
            if (resourceComputeEnvironment is not null &&
                resourceComputeEnvironment != this &&
                resourceComputeEnvironment != OwningComputeEnvironment)
            {
                continue;
            }

            // Configure OTLP for resources if dashboard is enabled
            if (DashboardEnabled && Dashboard?.Resource.OtlpGrpcEndpoint is EndpointReference otlpGrpcEndpoint)
            {
                ConfigureOtlp(r, otlpGrpcEndpoint);
            }

            // Create a Kubernetes compute resource for the resource
            var serviceResource = await environmentContext.CreateKubernetesResourceAsync(r, executionContext, cancellationToken).ConfigureAwait(false);
            serviceResource.AddPrintSummaryStep();

            // Add deployment target annotation to the resource.
            // Use the resource's actual compute environment (which may be a parent
            // like AzureKubernetesEnvironmentResource) so that GetDeploymentTargetAnnotation
            // can match it correctly during publish.
            var computeEnvForAnnotation = resourceComputeEnvironment ?? targetComputeEnvironment;
            r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
            {
                ComputeEnvironment = computeEnvForAnnotation,
                ContainerRegistry = containerRegistry
            });

            deploymentTargets[r] = serviceResource;
        }

        // Process Ingress resources
        await ProcessIngressResources(appModel, deploymentTargets, logger, context.CancellationToken).ConfigureAwait(false);

        // Process Gateway API resources
        await ProcessGatewayResources(appModel, deploymentTargets, logger, context.CancellationToken).ConfigureAwait(false);
    }

    private static IContainerRegistry? GetContainerRegistry(KubernetesEnvironmentResource environment, DistributedApplicationModel appModel)
    {
        // Check for explicit container registry reference annotation on the environment
        if (environment.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }

        // Check if there's a single container registry in the app model
        var registries = appModel.Resources.OfType<IContainerRegistry>().ToArray();
        if (registries.Length == 1)
        {
            return registries[0];
        }

        // Kubernetes has no local registry fallback — return null if no registry is configured.
        // The PushPrereq step will validate and error if a registry is required but not available.
        return null;
    }

    private static void ConfigureOtlp(IResource resource, EndpointReference otlpEndpoint)
    {
        if (resource is IResourceWithEnvironment resourceWithEnv && resource.Annotations.OfType<OtlpExporterAnnotation>().Any())
        {
            resourceWithEnv.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint] = otlpEndpoint;
                context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol] = "grpc";
                context.EnvironmentVariables[KnownOtelConfigNames.ServiceName] = resource.Name;
                return Task.CompletedTask;
            }));
        }
    }

    /// <summary>
    /// Resolves a <see cref="ReferenceExpression"/> at deploy time. Used by pipeline steps
    /// (e.g., TLS bootstrap, gateway address discovery) where parameter values are expected
    /// to be available. Falls back to the format string when a parameter is missing — these
    /// paths run after deploy and should not encounter unresolved parameters in practice.
    /// </summary>
    private static async Task<string> ResolveExpressionAtDeployTimeAsync(ReferenceExpression expression, CancellationToken cancellationToken)
    {
        try
        {
            return (await expression.GetValueAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (MissingParameterValueException)
        {
            return expression.Format;
        }
    }

    /// <summary>
    /// Resolves a <see cref="ReferenceExpression"/> for inclusion in a Kubernetes manifest
    /// produced by an ingress or gateway resource. When the expression wraps one or more
    /// <see cref="ParameterResource"/> instances that have no value at publish time
    /// (e.g., user-supplied parameters without defaults, or secrets), the expression is
    /// rendered with Helm template placeholders (such as <c>{{ .Values.parameters.ingress.ingressclass }}</c>)
    /// and the parameters are captured for deploy-time resolution into a values override file.
    /// </summary>
    /// <param name="expression">The reference expression to resolve.</param>
    /// <param name="owningResourceName">
    /// The name of the resource (ingress or gateway) that owns this expression. Used as the
    /// scoping segment in the generated Helm parameter path so multiple ingress/gateway resources
    /// can reuse the same parameter name without collision.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<string> ResolveExpressionAsync(ReferenceExpression expression, string owningResourceName, CancellationToken cancellationToken)
    {
        try
        {
            return (await expression.GetValueAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (MissingParameterValueException)
        {
            // One or more parameters in the expression have no value at publish time
            // (e.g., a parameter created via AddParameter("ingressclass") with no default,
            // or a secret parameter). Substitute each unresolved parameter with a Helm
            // template reference into values.yaml so the resulting manifest is a valid
            // Helm template and the value can be supplied at deploy time.
            var owningResourceKey = owningResourceName.ToHelmValuesSectionName();
            var args = new object[expression.ValueProviders.Count];

            for (var i = 0; i < expression.ValueProviders.Count; i++)
            {
                args[i] = await ResolveValueProviderAsync(
                    expression.ValueProviders[i],
                    owningResourceName,
                    owningResourceKey,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture, expression.Format, args);
        }
    }

    private async Task<string> ResolveValueProviderAsync(
        IValueProvider valueProvider,
        string owningResourceName,
        string owningResourceKey,
        CancellationToken cancellationToken)
    {
        if (valueProvider is ParameterResource parameter)
        {
            // Attempt to resolve this individual parameter first. The outer
            // MissingParameterValueException from the whole-expression resolve attempt
            // only tells us that *some* parameter in the expression was unresolved;
            // others (e.g., those with `publishValueAsDefault: true`) may still have
            // a value and should be inlined into the manifest rather than left as
            // Helm placeholders. This keeps the published chart maximally self-contained.
            try
            {
                return (await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
            }
            catch (MissingParameterValueException)
            {
                // Fall through to Helm-reference substitution below.
            }

            // Capture the parameter so HelmDeploymentEngine writes its resolved value to
            // the deploy-time override file, and ensure values.yaml has a placeholder so
            // `helm template` (and `aspire publish` consumers that don't deploy via Aspire)
            // can render the chart without `<no value>` substitutions.
            var section = parameter.Secret ? HelmExtensions.SecretsKey : HelmExtensions.ParametersKey;
            var valueKey = parameter.Name.ToHelmValuesSectionName();

            if (!CapturedHelmValues.Any(c =>
                    c.Section == section &&
                    c.ResourceKey == owningResourceKey &&
                    c.ValueKey == valueKey))
            {
                CapturedHelmValues.Add(new CapturedHelmValue(section, owningResourceKey, valueKey, parameter));
            }

            return parameter.Secret
                ? valueKey.ToHelmSecretExpression(owningResourceName)
                : valueKey.ToHelmParameterExpression(owningResourceName);
        }

        // For non-ParameterResource providers (string literals, endpoint references, etc.)
        // resolve normally. If a nested provider throws MissingParameterValueException
        // we intentionally let it propagate: silently substituting an empty string here
        // would produce invalid Kubernetes manifest fields (e.g., `secretName: ""`,
        // `hosts: [""]`) that only fail loudly at deploy time. Letting it throw surfaces
        // the unresolved-parameter problem during publish.
        return (await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
    }

    private async Task ProcessIngressResources(DistributedApplicationModel model, Dictionary<IResource, KubernetesResource> deploymentTargets, ILogger logger, CancellationToken cancellationToken)
    {
        var ingressResources = model.Resources
            .OfType<KubernetesIngressResource>()
            .Where(i => i.Parent == this);

        foreach (var ingressResource in ingressResources)
        {
            if (ingressResource.Paths.Count == 0 && ingressResource.DefaultBackend is null)
            {
                logger.LogWarning("Ingress '{IngressName}' has no path rules or default backend configured. Skipping.", ingressResource.Name);
                continue;
            }

            // Validate at publish time that every routed endpoint is flagged
            // external. We do this before BuildIngressObject runs so the user
            // sees the validation error before any partial work or warnings
            // about missing deployment targets are emitted.
            foreach (var path in ingressResource.Paths)
            {
                EndpointRoutingValidation.ThrowIfEndpointNotExternal(path.Endpoint, "Ingress", ingressResource.Name);
            }

            if (ingressResource.DefaultBackend is { } defaultBackend)
            {
                EndpointRoutingValidation.ThrowIfEndpointNotExternal(defaultBackend.Endpoint, "Ingress", ingressResource.Name);
            }

            var ingress = await BuildIngressObject(ingressResource, deploymentTargets, logger, cancellationToken).ConfigureAwait(false);
            if (ingress is not null)
            {
                ingressResource.GeneratedIngress = ingress;
            }
        }
    }

    private async Task<Ingress?> BuildIngressObject(
        KubernetesIngressResource ingressResource,
        Dictionary<IResource, KubernetesResource> deploymentTargets,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var ingress = new Ingress
        {
            Metadata =
            {
                Name = ingressResource.Name.ToKubernetesResourceName(),
            }
        };

        if (ingressResource.IngressClassName is not null)
        {
            ingress.Spec.IngressClassName = await ResolveExpressionAsync(ingressResource.IngressClassName, ingressResource.Name, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (key, value) in ingressResource.IngressAnnotations)
        {
            ingress.Metadata.Annotations[key] = await ResolveExpressionAsync(value, ingressResource.Name, cancellationToken).ConfigureAwait(false);
        }

        var pathsByHost = ingressResource.Paths.GroupBy(p => p.Host ?? string.Empty);

        foreach (var hostGroup in pathsByHost)
        {
            var rule = new IngressRuleV1();
            if (!string.IsNullOrEmpty(hostGroup.Key))
            {
                rule.Host = hostGroup.Key;
            }

            foreach (var pathRule in hostGroup)
            {
                var backend = ResolveIngressBackend(pathRule.Endpoint, deploymentTargets, ingressResource.Name, logger);
                if (backend is null)
                {
                    continue;
                }

                rule.Http.Paths.Add(new HttpIngressPathV1
                {
                    Path = pathRule.Path,
                    PathType = pathRule.PathType.ToKubernetesString(),
                    Backend = backend
                });
            }

            if (rule.Http.Paths.Count > 0)
            {
                ingress.Spec.Rules.Add(rule);
            }
        }

        if (ingressResource.DefaultBackend is not null)
        {
            var defaultBackend = ResolveIngressBackend(ingressResource.DefaultBackend.Endpoint, deploymentTargets, ingressResource.Name, logger);
            if (defaultBackend is not null)
            {
                ingress.Spec.DefaultBackend = defaultBackend;
            }
        }

        foreach (var tls in ingressResource.TlsConfigs)
        {
            var tlsEntry = new IngressTLSV1
            {
                SecretName = await ResolveExpressionAsync(tls.SecretName, ingressResource.Name, cancellationToken).ConfigureAwait(false),
            };

            foreach (var host in ingressResource.Hostnames)
            {
                tlsEntry.Hosts.Add(await ResolveExpressionAsync(host, ingressResource.Name, cancellationToken).ConfigureAwait(false));
            }

            ingress.Spec.Tls.Add(tlsEntry);
        }

        // Auto-generate rules for TLS hosts that don't have explicit routes.
        if (ingress.Spec.DefaultBackend is not null)
        {
            var hostsWithRules = new HashSet<string>(
                ingress.Spec.Rules
                    .Where(r => r.Host is not null)
                    .Select(r => r.Host!),
                StringComparer.OrdinalIgnoreCase);

            foreach (var tls in ingressResource.TlsConfigs)
            {
                foreach (var host in ingressResource.Hostnames)
                {
                    var resolvedHost = await ResolveExpressionAsync(host, ingressResource.Name, cancellationToken).ConfigureAwait(false);
                    if (!hostsWithRules.Contains(resolvedHost))
                    {
                        ingress.Spec.Rules.Add(new IngressRuleV1
                        {
                            Host = resolvedHost,
                            Http = new HttpIngressRuleValueV1
                            {
                                Paths =
                                {
                                    new HttpIngressPathV1
                                    {
                                        Path = "/",
                                        PathType = IngressPathType.Prefix.ToKubernetesString(),
                                        Backend = new IngressBackendV1
                                        {
                                            Service = new IngressServiceBackendV1
                                            {
                                                Name = ingress.Spec.DefaultBackend.Service.Name,
                                                Port = new ServiceBackendPortV1
                                                {
                                                    Name = ingress.Spec.DefaultBackend.Service.Port.Name,
                                                    Number = ingress.Spec.DefaultBackend.Service.Port.Number
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        });
                    }
                }
            }
        }

        if (ingress.Spec.Rules.Count == 0 && ingress.Spec.DefaultBackend is null)
        {
            logger.LogWarning("Ingress '{IngressName}' produced no valid rules or default backend. Skipping.", ingressResource.Name);
            return null;
        }

        return ingress;
    }

    private static IngressBackendV1? ResolveIngressBackend(
        EndpointReference endpointRef,
        Dictionary<IResource, KubernetesResource> deploymentTargets,
        string ingressName,
        ILogger logger)
    {
        var targetResource = endpointRef.Resource;

        if (!deploymentTargets.TryGetValue(targetResource, out var k8sResource))
        {
            logger.LogWarning(
                "Ingress '{IngressName}' references endpoint on resource '{ResourceName}' which has no Kubernetes deployment target. Skipping this route.",
                ingressName, targetResource.Name);
            return null;
        }

        var endpointName = endpointRef.EndpointName;
        if (!k8sResource.EndpointMappings.TryGetValue(endpointName, out var mapping))
        {
            logger.LogWarning(
                "Ingress '{IngressName}' references endpoint '{EndpointName}' on resource '{ResourceName}' but no matching endpoint mapping was found. Skipping this route.",
                ingressName, endpointName, targetResource.Name);
            return null;
        }

        return new IngressBackendV1
        {
            Service = new IngressServiceBackendV1
            {
                Name = k8sResource.Service?.Metadata.Name ?? targetResource.Name.ToServiceName(),
                Port = new ServiceBackendPortV1
                {
                    Name = mapping.Name
                }
            }
        };
    }

    private async Task ProcessGatewayResources(DistributedApplicationModel model, Dictionary<IResource, KubernetesResource> deploymentTargets, ILogger logger, CancellationToken cancellationToken)
    {
        var gatewayResources = model.Resources
            .OfType<KubernetesGatewayResource>()
            .Where(g => g.Parent == this);

        foreach (var gatewayResource in gatewayResources)
        {
            if (gatewayResource.Routes.Count == 0)
            {
                logger.LogWarning("Gateway '{GatewayName}' has no routes configured. Skipping.", gatewayResource.Name);
                continue;
            }

            // Validate that every routed endpoint is flagged external before
            // we materialize the Gateway and HTTPRoute objects.
            foreach (var route in gatewayResource.Routes)
            {
                EndpointRoutingValidation.ThrowIfEndpointNotExternal(route.Endpoint, "Gateway", gatewayResource.Name);
            }

            await BuildGatewayObjects(gatewayResource, deploymentTargets, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BuildGatewayObjects(
        KubernetesGatewayResource gatewayResource,
        Dictionary<IResource, KubernetesResource> deploymentTargets,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (gatewayResource.GatewayClassName is null)
        {
            throw new InvalidOperationException(
                $"Gateway '{gatewayResource.Name}' must have a GatewayClassName set via WithGatewayClass(). " +
                $"The Gateway API requires a gatewayClassName to select the controller implementation.");
        }

        var gatewayName = gatewayResource.Name.ToKubernetesResourceName();

        var gateway = new GatewayV1
        {
            Metadata = { Name = gatewayName }
        };

        gateway.Spec.GatewayClassName = await ResolveExpressionAsync(gatewayResource.GatewayClassName, gatewayResource.Name, cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in gatewayResource.GatewayAnnotations)
        {
            gateway.Metadata.Annotations[key] = await ResolveExpressionAsync(value, gatewayResource.Name, cancellationToken).ConfigureAwait(false);
        }

        gateway.Spec.Listeners.Add(new GatewayListenerV1
        {
            Name = "http",
            Protocol = "HTTP",
            Port = 80,
            AllowedRoutes = new GatewayAllowedRoutesV1
            {
                Namespaces = new GatewayRouteNamespacesV1 { From = "Same" }
            }
        });

        var tlsListenerIndex = 0;
        foreach (var tls in gatewayResource.TlsConfigs)
        {
            var resolvedSecretName = await ResolveExpressionAsync(tls.SecretName, gatewayResource.Name, cancellationToken).ConfigureAwait(false);

            if (gatewayResource.Hostnames.Count == 0)
            {
                // No hostnames specified — create an HTTPS listener without a hostname restriction.
                // The hostname will be discovered from the Gateway's assigned address after deployment
                // and patched onto the listener to enable cert-manager certificate issuance.
                var listenerName = tlsListenerIndex == 0 ? "https" : $"https-{tlsListenerIndex}";
                tlsListenerIndex++;

                gateway.Spec.Listeners.Add(new GatewayListenerV1
                {
                    Name = listenerName,
                    Protocol = "HTTPS",
                    Port = 443,
                    Tls = new GatewayTlsConfigV1
                    {
                        Mode = "Terminate",
                        CertificateRefs = { new GatewayCertificateRefV1 { Name = resolvedSecretName } }
                    },
                    AllowedRoutes = new GatewayAllowedRoutesV1
                    {
                        Namespaces = new GatewayRouteNamespacesV1 { From = "Same" }
                    }
                });
            }
            else
            {
                foreach (var host in gatewayResource.Hostnames)
                {
                    var listenerName = tlsListenerIndex == 0 ? "https" : $"https-{tlsListenerIndex}";
                    tlsListenerIndex++;

                    var resolvedHost = await ResolveExpressionAsync(host, gatewayResource.Name, cancellationToken).ConfigureAwait(false);

                    gateway.Spec.Listeners.Add(new GatewayListenerV1
                    {
                        Name = listenerName,
                        Protocol = "HTTPS",
                        Port = 443,
                        Hostname = resolvedHost,
                        Tls = new GatewayTlsConfigV1
                        {
                            Mode = "Terminate",
                            CertificateRefs = { new GatewayCertificateRefV1 { Name = resolvedSecretName } }
                        },
                        AllowedRoutes = new GatewayAllowedRoutesV1
                        {
                            Namespaces = new GatewayRouteNamespacesV1 { From = "Same" }
                        }
                    });
                }
            }
        }

        gatewayResource.GeneratedGateway = gateway;

        var routesByHost = gatewayResource.Routes.GroupBy(r => r.Host ?? string.Empty);

        foreach (var hostGroup in routesByHost)
        {
            var routeName = string.IsNullOrEmpty(hostGroup.Key)
                ? $"{gatewayName}-route"
                : $"{gatewayName}-{hostGroup.Key.Replace(".", "-").Replace("*", "wildcard").ToLowerInvariant()}-route";

            var httpRoute = new HttpRouteV1
            {
                Metadata = { Name = routeName }
            };

            httpRoute.Spec.ParentRefs.Add(new HttpRouteParentRefV1 { Name = gatewayName });

            if (!string.IsNullOrEmpty(hostGroup.Key))
            {
                httpRoute.Spec.Hostnames.Add(hostGroup.Key);
            }

            foreach (var route in hostGroup)
            {
                var backendRef = ResolveGatewayBackendRef(route.Endpoint, deploymentTargets, gatewayResource.Name, logger);
                if (backendRef is null)
                {
                    continue;
                }

                var pathType = route.PathType switch
                {
                    GatewayPathMatchType.Exact => "Exact",
                    GatewayPathMatchType.PathPrefix => "PathPrefix",
                    GatewayPathMatchType.RegularExpression => "RegularExpression",
                    _ => throw new InvalidOperationException($"Unknown gateway path match type '{route.PathType}'.")
                };

                var rule = new HttpRouteRuleV1();
                rule.Matches.Add(new HttpRouteMatchV1
                {
                    Path = new HttpRoutePathMatchV1
                    {
                        Type = pathType,
                        Value = route.Path
                    }
                });
                rule.BackendRefs.Add(backendRef);
                httpRoute.Spec.Rules.Add(rule);
            }

            if (httpRoute.Spec.Rules.Count > 0)
            {
                gatewayResource.GeneratedHttpRoutes.Add(httpRoute);
            }
        }
    }

    private static HttpRouteBackendRefV1? ResolveGatewayBackendRef(
        EndpointReference endpointRef,
        Dictionary<IResource, KubernetesResource> deploymentTargets,
        string gatewayName,
        ILogger logger)
    {
        var targetResource = endpointRef.Resource;

        if (!deploymentTargets.TryGetValue(targetResource, out var k8sResource))
        {
            logger.LogWarning(
                "Gateway '{GatewayName}' references endpoint on resource '{ResourceName}' which has no Kubernetes deployment target. Skipping this route.",
                gatewayName, targetResource.Name);
            return null;
        }

        var endpointName = endpointRef.EndpointName;
        if (!k8sResource.EndpointMappings.TryGetValue(endpointName, out var mapping))
        {
            logger.LogWarning(
                "Gateway '{GatewayName}' references endpoint '{EndpointName}' on resource '{ResourceName}' but no matching endpoint mapping was found. Skipping this route.",
                gatewayName, endpointName, targetResource.Name);
            return null;
        }

        var portValue = mapping.ServicePort ?? mapping.Port;
        if (!int.TryParse(portValue.Value?.ToString(), out var portNumber))
        {
            portNumber = 8080;
        }

        return new HttpRouteBackendRefV1
        {
            Name = k8sResource.Service?.Metadata.Name ?? targetResource.Name.ToServiceName(),
            Port = portNumber
        };
    }

    private static HashSet<(ReferenceExpression SecretName, ReferenceExpression Hostname)> CollectTlsSecrets(DistributedApplicationModel model, KubernetesEnvironmentResource environment)
    {
        var tlsSecrets = new HashSet<(ReferenceExpression SecretName, ReferenceExpression Hostname)>();

        foreach (var gateway in model.Resources.OfType<KubernetesGatewayResource>().Where(g => g.Parent == environment))
        {
            foreach (var tls in gateway.TlsConfigs)
            {
                foreach (var host in gateway.Hostnames)
                {
                    tlsSecrets.Add((tls.SecretName, host));
                }
            }
        }

        foreach (var ingress in model.Resources.OfType<KubernetesIngressResource>().Where(i => i.Parent == environment))
        {
            foreach (var tls in ingress.TlsConfigs)
            {
                foreach (var host in ingress.Hostnames)
                {
                    tlsSecrets.Add((tls.SecretName, host));
                }
            }
        }

        return tlsSecrets;
    }

    /// <summary>
    /// Collects gateway TLS configurations that have no hostnames specified and need
    /// the assigned FQDN to be discovered from the Gateway's status after deployment.
    /// </summary>
    private static List<(KubernetesGatewayResource Gateway, ReferenceExpression SecretName)> CollectGatewaysNeedingFqdnDiscovery(
        DistributedApplicationModel model,
        KubernetesEnvironmentResource environment)
    {
        var results = new List<(KubernetesGatewayResource Gateway, ReferenceExpression SecretName)>();

        foreach (var gateway in model.Resources.OfType<KubernetesGatewayResource>().Where(g => g.Parent == environment))
        {
            foreach (var tls in gateway.TlsConfigs)
            {
                if (gateway.Hostnames.Count == 0)
                {
                    results.Add((gateway, tls.SecretName));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Collects Gateway resources that have any TLS configuration, regardless of whether
    /// hostnames are explicitly set. Used by the pre-deploy cleanup step that strips
    /// stale foreign field-manager ownership.
    /// </summary>
    private static List<KubernetesGatewayResource> CollectGatewaysWithTls(
        DistributedApplicationModel model,
        KubernetesEnvironmentResource environment)
    {
        var results = new List<KubernetesGatewayResource>();

        foreach (var gateway in model.Resources.OfType<KubernetesGatewayResource>().Where(g => g.Parent == environment))
        {
            if (gateway.TlsConfigs.Count > 0)
            {
                results.Add(gateway);
            }
        }

        return results;
    }

    /// <summary>
    /// Discovers the assigned FQDN from the Gateway's status, patches the HTTPS listener
    /// to include the hostname, and creates a bootstrap TLS secret so cert-manager can
    /// issue a real certificate.
    /// </summary>
    private static async Task DiscoverFqdnAndBootstrapTlsAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        List<(KubernetesGatewayResource Gateway, ReferenceExpression SecretName)> gatewaysNeedingDiscovery)
    {
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        foreach (var (gateway, secretNameExpr) in gatewaysNeedingDiscovery)
        {
            var gatewayName = gateway.Name.ToKubernetesResourceName();
            var secretName = await ResolveExpressionAtDeployTimeAsync(secretNameExpr, context.CancellationToken).ConfigureAwait(false);

            // Pre-create a placeholder bootstrap TLS secret BEFORE waiting for the Gateway
            // address. Some controllers (notably Azure Application Gateway for Containers)
            // refuse to program a Gateway whose HTTPS listener references a non-existent
            // Secret — they log "Secret 'X' not found" and stop reconciling. That creates a
            // deadlock with the FQDN discovery flow, which itself waits for the Gateway to
            // be programmed. Uploading a self-signed placeholder up front breaks the
            // chicken-and-egg: the Gateway can program with the placeholder cert, get an
            // address, and we then patch the listener hostname so cert-manager can replace
            // the placeholder with a real certificate.
            await EnsureBootstrapTlsSecretAsync(
                secretName,
                hostname: "bootstrap.invalid",
                @namespace,
                environment,
                context).ConfigureAwait(false);

            // Poll for the Gateway's assigned hostname address.
            // We use -o json and parse the full status to select Hostname-type addresses,
            // since some controllers return IP addresses which are not valid for TLS hostnames.
            context.Logger.LogInformation(
                "Waiting for Gateway '{GatewayName}' to be assigned a hostname address...", gatewayName);

            var discoveredFqdn = await DiscoverGatewayFqdnAsync(
                gatewayName, @namespace, environment, context).ConfigureAwait(false);

            if (string.IsNullOrEmpty(discoveredFqdn))
            {
                // Hard failure rather than a logged warning: when the user has TLS configured
                // without an explicit hostname, the deployment is only meaningful once the
                // listener hostname is patched (cert-manager's gateway shim issues no
                // certificate for a hostname-less HTTPS listener). Silently continuing
                // produces an apparently-successful deploy that never serves valid TLS, which
                // is far worse than failing visibly here. The user can fix this by either
                // waiting for their controller to assign an address sooner or by passing an
                // explicit hostname via WithHostname() / WithTls(hostname: ...).
                throw new InvalidOperationException(
                    $"Gateway '{gatewayName}' was not assigned a hostname address within the discovery timeout. " +
                    "TLS hostname discovery cannot complete. Either retry the deploy (the controller may still be " +
                    "provisioning the address) or set an explicit hostname via WithHostname().");
            }

            context.Logger.LogInformation(
                "Gateway '{GatewayName}' assigned address: {Fqdn}. Patching HTTPS listener(s) and bootstrapping TLS.",
                gatewayName, discoveredFqdn);

            // Find HTTPS listeners without a hostname by parsing the full Gateway JSON.
            var httpsListenerIndices = await FindHostnamelessHttpsListeners(
                gatewayName, @namespace, environment, context).ConfigureAwait(false);

            if (httpsListenerIndices.Count == 0)
            {
                context.Logger.LogWarning(
                    "No HTTPS listeners without hostname found on Gateway '{GatewayName}'. Skipping hostname patch.",
                    gatewayName);
            }
            else
            {
                // Build the JSON patch using proper serialization to avoid injection issues.
                var patchOperations = httpsListenerIndices.Select(idx => new
                {
                    op = "add",
                    path = $"/spec/listeners/{idx}/hostname",
                    value = discoveredFqdn
                });
                var patchJson = System.Text.Json.JsonSerializer.Serialize(patchOperations);

                // Write patch to a temp file to avoid shell escaping issues with kubectl -p
                var patchTempDir = Directory.CreateTempSubdirectory(".aspire-gateway-patch");
                try
                {
                    var patchFilePath = Path.Combine(patchTempDir.FullName, "patch.json");
                    await File.WriteAllTextAsync(patchFilePath, patchJson, context.CancellationToken).ConfigureAwait(false);

                    // Use --field-manager=helm so Helm becomes the registered owner of the
                    // listener hostname field. Without this, kubectl defaults the field manager
                    // to "kubectl-patch", and a subsequent `helm upgrade` (which uses server-side
                    // apply with field manager "helm") fails on the next deploy with:
                    //   conflict with "kubectl-patch" using gateway.networking.k8s.io/v1:
                    //   .spec.listeners[name="https"].hostname
                    // Server-side apply Apply operations conflict with foreign Update operations
                    // recorded in managedFields, but matching manager names do not conflict.
                    // See https://kubernetes.io/docs/reference/using-api/server-side-apply/#conflicts
                    var patchArgs = $"patch gateway {gatewayName} --namespace {@namespace} --type=json --field-manager=helm --patch-file \"{patchFilePath}\"";
                    if (environment.KubeConfigPath is not null)
                    {
                        patchArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
                    }

                    var (patchResult, patchDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
                    {
                        Arguments = patchArgs,
                        ThrowOnNonZeroReturnCode = false,
                        InheritEnv = true,
                        OnOutputData = line => context.Logger.LogDebug("{Line}", line),
                        OnErrorData = line => context.Logger.LogDebug("{Line}", line)
                    });

                    await using (patchDisposable.ConfigureAwait(false))
                    {
                        var patchExitResult = await patchResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                        if (patchExitResult.ExitCode != 0)
                        {
                            context.Logger.LogWarning(
                                "Failed to patch Gateway '{GatewayName}' with hostname '{Hostname}' (exit code {ExitCode}). " +
                                "You may need to redeploy with an explicit hostname via WithHostname().",
                                gatewayName, discoveredFqdn, patchExitResult.ExitCode);
                            continue;
                        }
                    }
                }
                finally
                {
                    try { patchTempDir.Delete(recursive: true); } catch { }
                }

                // Transfer field ownership to Helm using server-side apply with a minimal
                // Gateway manifest so subsequent Helm deploys don't encounter SSA conflicts.
                // We construct a minimal spec rather than re-applying kubectl get output,
                // which would include server-populated fields (status, resourceVersion, etc.).
                await TransferGatewayFieldOwnership(
                    gatewayName, @namespace, environment, context).ConfigureAwait(false);
            }

            // Bootstrap TLS secret was already pre-created above. Nothing further to do
            // here — cert-manager will replace it with a real certificate once the
            // listener hostname is detected on the Gateway.
        }
    }

    /// <summary>
    /// Creates a self-signed bootstrap TLS secret with the given hostname as the CN/SAN
    /// if it doesn't already exist. Used by the FQDN discovery flow to break the
    /// chicken-and-egg between Gateway controllers (which need the secret to program the
    /// Gateway) and FQDN discovery (which needs the Gateway to be programmed).
    /// </summary>
    private static async Task EnsureBootstrapTlsSecretAsync(
        string secretName,
        string hostname,
        string @namespace,
        KubernetesEnvironmentResource environment,
        PipelineStepContext context)
    {
        var checkArgs = $"get secret {secretName} --namespace {@namespace}";
        if (environment.KubeConfigPath is not null)
        {
            checkArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
        }

        var (checkResult, checkDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            Arguments = checkArgs,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = _ => { },
            OnErrorData = _ => { }
        });

        await using (checkDisposable.ConfigureAwait(false))
        {
            var result = await checkResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                context.Logger.LogInformation("TLS secret '{SecretName}' already exists, skipping bootstrap.", secretName);
                return;
            }
        }

        context.Logger.LogInformation("Creating bootstrap TLS secret '{SecretName}' for '{Hostname}'.", secretName, hostname);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certRequest = new CertificateRequest($"CN={hostname}", ecdsa, HashAlgorithmName.SHA256);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostname);
        certRequest.CertificateExtensions.Add(sanBuilder.Build());
        using var cert = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var certPem = cert.ExportCertificatePem();
        var keyPem = ecdsa.ExportECPrivateKeyPem();

        var tempDir = Directory.CreateTempSubdirectory(".aspire-tls-discovery");
        try
        {
            var certPath = Path.Combine(tempDir.FullName, "tls.crt");
            var keyPath = Path.Combine(tempDir.FullName, "tls.key");
            await File.WriteAllTextAsync(certPath, certPem, context.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(keyPath, keyPem, context.CancellationToken).ConfigureAwait(false);

            var createArgs = $"create secret tls {secretName} --cert=\"{certPath}\" --key=\"{keyPath}\" --namespace {@namespace}";
            if (environment.KubeConfigPath is not null)
            {
                createArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
            }

            var (createResult, createDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = createArgs,
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = line => context.Logger.LogDebug("{Line}", line),
                OnErrorData = line => context.Logger.LogDebug("{Line}", line)
            });

            await using (createDisposable.ConfigureAwait(false))
            {
                var createExitResult = await createResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (createExitResult.ExitCode != 0)
                {
                    context.Logger.LogWarning("Failed to create bootstrap TLS secret '{SecretName}' (exit code {ExitCode}).", secretName, createExitResult.ExitCode);
                }
                else
                {
                    context.Logger.LogInformation(
                        "Bootstrap TLS secret '{SecretName}' created for '{Hostname}'. " +
                        "cert-manager will replace this with a real certificate once the hostname is detected on the Gateway listener.",
                        secretName, hostname);
                }
            }
        }
        finally
        {
            DeleteTempDirSafely(tempDir, context.Logger);
        }
    }

    // Best-effort cleanup for kubectl/cert temp directories. Swallowing OperationCanceledException
    // here would mask shutdown signals; instead narrow to file-system failures (a transient
    // antivirus lock or permission issue) and surface them at debug level so the next deploy
    // attempt - or `aspire run` with verbose logging - can diagnose if leakage occurs.
    private static void DeleteTempDirSafely(DirectoryInfo tempDir, ILogger logger)
    {
        try
        {
            tempDir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to delete temporary directory '{TempDir}'.", tempDir.FullName);
        }
    }

    /// <summary>
    /// Polls for the Gateway's assigned hostname address using a Polly retry pipeline.
    /// Retries up to 60 times with 5-second delays (5 minutes total).
    /// </summary>
    private static async Task<string?> DiscoverGatewayFqdnAsync(
        string gatewayName,
        string @namespace,
        KubernetesEnvironmentResource environment,
        PipelineStepContext context)
    {
        // AGC and other Gateway controllers can take several minutes to assign an address
        // after the Gateway is created (AGC has been observed taking 5-10 minutes when AGC
        // and the cluster are freshly provisioned in the same deploy). Allow up to ~15
        // minutes (180 attempts × 5s) before giving up, matching the wait budget our E2E
        // tests use for the same condition.
        var pipeline = new ResiliencePipelineBuilder<string?>()
            .AddRetry(new RetryStrategyOptions<string?>
            {
                MaxRetryAttempts = 179,
                Delay = TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<string?>().HandleResult(r => r is null),
                OnRetry = args =>
                {
                    context.Logger.LogDebug(
                        "Gateway '{GatewayName}' address not yet available (attempt {Attempt}).",
                        gatewayName, args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();

        return await pipeline.ExecuteAsync(async ct =>
        {
            var getArgs = $"get gateway {gatewayName} --namespace {@namespace} -o json";
            if (environment.KubeConfigPath is not null)
            {
                getArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
            }

            var stdout = new List<string>();
            var (getResult, getDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = getArgs,
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = stdout.Add,
                OnErrorData = _ => { }
            });

            await using (getDisposable.ConfigureAwait(false))
            {
                var result = await getResult.WaitAsync(ct).ConfigureAwait(false);
                if (result.ExitCode == 0 && stdout.Count > 0)
                {
                    return ExtractHostnameFromGatewayJson(string.Join("", stdout));
                }
            }

            return null;
        }, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts the first Hostname-type address from Gateway JSON status.
    /// Returns null if no hostname address is found (e.g., only IP addresses).
    /// </summary>
    private static string? ExtractHostnameFromGatewayJson(string gatewayJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(gatewayJson);
            if (doc.RootElement.TryGetProperty("status", out var status) &&
                status.TryGetProperty("addresses", out var addresses))
            {
                // Prefer Hostname-type addresses over IPAddress
                foreach (var addr in addresses.EnumerateArray())
                {
                    if (addr.TryGetProperty("type", out var type) &&
                        string.Equals(type.GetString(), "Hostname", StringComparison.OrdinalIgnoreCase) &&
                        addr.TryGetProperty("value", out var value))
                    {
                        var hostname = value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(hostname))
                        {
                            return hostname;
                        }
                    }
                }

                // Fall back to any address that looks like a DNS name (contains a dot, no colons)
                foreach (var addr in addresses.EnumerateArray())
                {
                    if (addr.TryGetProperty("value", out var value))
                    {
                        var addrValue = value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(addrValue) && addrValue.Contains('.') && !addrValue.Contains(':'))
                        {
                            return addrValue;
                        }
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Gateway JSON was malformed
        }

        return null;
    }

    /// <summary>
    /// Finds HTTPS listener indices that don't have a hostname set, by parsing the
    /// full Gateway JSON from kubectl.
    /// </summary>
    private static async Task<List<int>> FindHostnamelessHttpsListeners(
        string gatewayName,
        string @namespace,
        KubernetesEnvironmentResource environment,
        PipelineStepContext context)
    {
        var getArgs = $"get gateway {gatewayName} --namespace {@namespace} -o json";
        if (environment.KubeConfigPath is not null)
        {
            getArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
        }

        var stdout = new List<string>();
        var (getResult, getDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            Arguments = getArgs,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = stdout.Add,
            OnErrorData = _ => { }
        });

        var indices = new List<int>();
        await using (getDisposable.ConfigureAwait(false))
        {
            var result = await getResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && stdout.Count > 0)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(string.Join("", stdout));
                    if (doc.RootElement.TryGetProperty("spec", out var spec) &&
                        spec.TryGetProperty("listeners", out var listeners))
                    {
                        for (var i = 0; i < listeners.GetArrayLength(); i++)
                        {
                            var listener = listeners[i];
                            if (listener.TryGetProperty("protocol", out var protocol) &&
                                string.Equals(protocol.GetString(), "HTTPS", StringComparison.OrdinalIgnoreCase) &&
                                !listener.TryGetProperty("hostname", out _))
                            {
                                indices.Add(i);
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Fall back to assuming index 1 (HTTP=0, HTTPS=1)
                    indices.Add(1);
                }
            }
            else
            {
                // Fall back to assuming index 1
                indices.Add(1);
            }
        }

        return indices;
    }

    /// <summary>
    /// Pre-helm-deploy step: removes any non-helm Update entries from the
    /// <c>managedFields</c> of existing Gateways with TLS configuration. Older Aspire
    /// builds patched the listener hostname with kubectl's default field manager
    /// (<c>kubectl-patch</c>), and that Update entry persists in the resource's
    /// <c>managedFields</c> after later builds switched to <c>--field-manager=helm</c>.
    /// Server-side Apply by helm conflicts with the foreign Update ownership of
    /// <c>.spec.listeners[name="https"].hostname</c> until the foreign entry is removed,
    /// so we strip it preemptively. No-op when the Gateway doesn't yet exist (first
    /// deploy), when the cluster is unreachable, or when only helm-owned managedFields
    /// entries are present.
    /// See https://kubernetes.io/docs/reference/using-api/server-side-apply/#clearing-managedfields
    /// </summary>
    private static async Task CleanupGatewayFieldOwnershipAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        List<KubernetesGatewayResource> gateways)
    {
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        foreach (var gateway in gateways)
        {
            var gatewayName = gateway.Name.ToKubernetesResourceName();

            var getArgs = $"get gateway {gatewayName} --namespace {@namespace} --show-managed-fields -o json --ignore-not-found";
            if (environment.KubeConfigPath is not null)
            {
                getArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
            }

            var stdout = new List<string>();
            var (getResult, getDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = getArgs,
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = stdout.Add,
                OnErrorData = line => context.Logger.LogDebug("{Line}", line)
            });

            int getExit;
            await using (getDisposable.ConfigureAwait(false))
            {
                getExit = (await getResult.WaitAsync(context.CancellationToken).ConfigureAwait(false)).ExitCode;
            }

            if (getExit != 0 || stdout.Count == 0)
            {
                // Gateway doesn't exist yet (first deploy) or kubectl error — nothing to do.
                context.Logger.LogDebug("Gateway '{GatewayName}' not found or unreadable; skipping field-ownership cleanup.", gatewayName);
                continue;
            }

            // Identify managedFields entries to remove: any Update operation by a manager
            // other than "helm" that owns fields under .spec.listeners. We descend in
            // reverse index order so JSON patch path indices remain stable.
            List<int> indicesToRemove;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.Join("", stdout));
                indicesToRemove = FindForeignListenerOwnershipIndices(doc.RootElement);
            }
            catch (System.Text.Json.JsonException ex)
            {
                context.Logger.LogDebug(ex, "Could not parse Gateway '{GatewayName}' JSON for field-ownership cleanup.", gatewayName);
                continue;
            }

            if (indicesToRemove.Count == 0)
            {
                continue;
            }

            // Build a JSON Patch removing each stale entry. Process highest index first so
            // earlier removals don't shift the indices of pending operations.
            var ops = indicesToRemove
                .OrderByDescending(i => i)
                .Select(i => new
                {
                    op = "remove",
                    path = $"/metadata/managedFields/{i}"
                });
            var patchJson = System.Text.Json.JsonSerializer.Serialize(ops);

            var patchTempDir = Directory.CreateTempSubdirectory(".aspire-gateway-fields");
            try
            {
                var patchFilePath = Path.Combine(patchTempDir.FullName, "patch.json");
                await File.WriteAllTextAsync(patchFilePath, patchJson, context.CancellationToken).ConfigureAwait(false);

                var patchArgs = $"patch gateway {gatewayName} --namespace {@namespace} --type=json --patch-file \"{patchFilePath}\"";
                if (environment.KubeConfigPath is not null)
                {
                    patchArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
                }

                var (patchResult, patchDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
                {
                    Arguments = patchArgs,
                    ThrowOnNonZeroReturnCode = false,
                    InheritEnv = true,
                    OnOutputData = line => context.Logger.LogDebug("{Line}", line),
                    OnErrorData = line => context.Logger.LogDebug("{Line}", line)
                });

                await using (patchDisposable.ConfigureAwait(false))
                {
                    var patchExit = (await patchResult.WaitAsync(context.CancellationToken).ConfigureAwait(false)).ExitCode;
                    if (patchExit == 0)
                    {
                        context.Logger.LogInformation(
                            "Removed {Count} stale field-manager entr{Suffix} from Gateway '{GatewayName}' to allow helm upgrade.",
                            indicesToRemove.Count,
                            indicesToRemove.Count == 1 ? "y" : "ies",
                            gatewayName);
                    }
                    else
                    {
                        // Best-effort cleanup: if it fails, helm upgrade may still succeed
                        // (the user may have an explicit hostname matching the existing one)
                        // and if it doesn't, the failure surfaces clearly via helm.
                        context.Logger.LogDebug(
                            "Field-ownership cleanup on Gateway '{GatewayName}' returned exit code {ExitCode}; continuing.",
                            gatewayName, patchExit);
                    }
                }
            }
            finally
            {
                DeleteTempDirSafely(patchTempDir, context.Logger);
            }
        }
    }

    /// <summary>
    /// Returns the indices of <c>managedFields</c> entries that represent foreign
    /// (non-helm) Update operations owning fields under <c>.spec.listeners</c>. These
    /// entries are remnants of older builds that patched the Gateway with kubectl's
    /// default field manager and conflict with helm's server-side apply on subsequent
    /// upgrades.
    /// </summary>
    private static List<int> FindForeignListenerOwnershipIndices(System.Text.Json.JsonElement root)
    {
        var indices = new List<int>();

        if (!root.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("managedFields", out var managedFields) ||
            managedFields.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return indices;
        }

        for (var i = 0; i < managedFields.GetArrayLength(); i++)
        {
            var entry = managedFields[i];

            // Only target Update entries — Apply entries are part of normal SSA ownership
            // and removing them would be destructive.
            if (!entry.TryGetProperty("operation", out var op) ||
                !string.Equals(op.GetString(), "Update", StringComparison.Ordinal))
            {
                continue;
            }

            // Helm's own entries are safe — both Apply and any incidental Update use the
            // same manager name and therefore don't conflict with helm's next Apply.
            if (entry.TryGetProperty("manager", out var mgr) &&
                string.Equals(mgr.GetString(), "helm", StringComparison.Ordinal))
            {
                continue;
            }

            // Only remove entries that own a listener field — preserves status managers
            // like alb-controller which legitimately update .status. The fieldsV1 path
            // for a Gateway listener is shaped:
            //   {"f:spec":{"f:listeners":{"k:{\"name\":\"https\"}":{"f:hostname":{}}}}}
            if (entry.TryGetProperty("fieldsV1", out var fieldsV1) &&
                fieldsV1.TryGetProperty("f:spec", out var fSpec) &&
                fSpec.TryGetProperty("f:listeners", out _))
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    /// <summary>
    /// Transfers field ownership of the Gateway's patched hostname to Helm using server-side apply
    /// with a minimal Gateway manifest, avoiding server-populated fields like status and resourceVersion.
    /// </summary>
    private static async Task TransferGatewayFieldOwnership(
        string gatewayName,
        string @namespace,
        KubernetesEnvironmentResource environment,
        PipelineStepContext context)
    {
        // Build a minimal Gateway JSON with only the fields needed for ownership transfer.
        // We read the current Gateway, strip server-populated fields, update the hostname,
        // and re-apply with Helm's field manager.
        var getArgs = $"get gateway {gatewayName} --namespace {@namespace} -o json";
        if (environment.KubeConfigPath is not null)
        {
            getArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
        }

        var stdout = new List<string>();
        var (getResult, getDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            Arguments = getArgs,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = stdout.Add,
            OnErrorData = _ => { }
        });

        await using (getDisposable.ConfigureAwait(false))
        {
            var exitResult = await getResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            if (exitResult.ExitCode != 0 || stdout.Count == 0)
            {
                context.Logger.LogDebug("Could not read Gateway for field ownership transfer.");
                return;
            }
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.Join("", stdout));
            var root = doc.RootElement;

            // Build a minimal manifest: apiVersion, kind, metadata (name, namespace, annotations, labels), spec
            var metadataNode = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = gatewayName,
                ["namespace"] = @namespace
            };

            // Preserve annotations and labels from the current Gateway
            if (root.TryGetProperty("metadata", out var metadata))
            {
                if (metadata.TryGetProperty("annotations", out var annotations))
                {
                    metadataNode["annotations"] = System.Text.Json.Nodes.JsonNode.Parse(annotations.GetRawText());
                }

                if (metadata.TryGetProperty("labels", out var labels))
                {
                    metadataNode["labels"] = System.Text.Json.Nodes.JsonNode.Parse(labels.GetRawText());
                }
            }

            var minimal = new System.Text.Json.Nodes.JsonObject
            {
                ["apiVersion"] = root.GetProperty("apiVersion").GetString(),
                ["kind"] = root.GetProperty("kind").GetString(),
                ["metadata"] = metadataNode
            };

            // Copy the spec as-is (it already has the patched hostname from the previous step)
            if (root.TryGetProperty("spec", out var spec))
            {
                minimal["spec"] = System.Text.Json.Nodes.JsonNode.Parse(spec.GetRawText());
            }

            var tempDir = Directory.CreateTempSubdirectory(".aspire-gateway-ownership");
            try
            {
                var manifestPath = Path.Combine(tempDir.FullName, "gateway.json");
                await File.WriteAllTextAsync(manifestPath, minimal.ToJsonString(), context.CancellationToken).ConfigureAwait(false);

                var applyArgs = $"apply --server-side --field-manager=helm --force-conflicts -f \"{manifestPath}\"";
                if (environment.KubeConfigPath is not null)
                {
                    applyArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
                }

                var (applyResult, applyDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
                {
                    Arguments = applyArgs,
                    ThrowOnNonZeroReturnCode = false,
                    InheritEnv = true,
                    OnOutputData = line => context.Logger.LogDebug("{Line}", line),
                    OnErrorData = line => context.Logger.LogDebug("{Line}", line)
                });

                await using (applyDisposable.ConfigureAwait(false))
                {
                    var applyExitResult = await applyResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    if (applyExitResult.ExitCode != 0)
                    {
                        context.Logger.LogDebug(
                            "Failed to transfer field ownership to Helm (exit code {ExitCode}). " +
                            "Subsequent deploys with an explicit hostname may require --force.",
                            applyExitResult.ExitCode);
                    }
                }
            }
            finally
            {
                DeleteTempDirSafely(tempDir, context.Logger);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            context.Logger.LogDebug(ex, "Failed to parse Gateway JSON for field ownership transfer.");
        }
    }

    private static async Task BootstrapTlsSecretsAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        HashSet<(ReferenceExpression SecretName, ReferenceExpression Hostname)> tlsSecrets)
    {
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        foreach (var (secretNameExpr, hostnameExpr) in tlsSecrets)
        {
            var secretName = await ResolveExpressionAtDeployTimeAsync(secretNameExpr, context.CancellationToken).ConfigureAwait(false);
            var hostname = await ResolveExpressionAtDeployTimeAsync(hostnameExpr, context.CancellationToken).ConfigureAwait(false);

            var checkArgs = $"get secret {secretName} --namespace {@namespace}";
            if (environment.KubeConfigPath is not null)
            {
                checkArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
            }

            var (checkResult, checkDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = checkArgs,
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = _ => { },
                OnErrorData = _ => { }
            });

            await using (checkDisposable.ConfigureAwait(false))
            {
                var result = await checkResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                {
                    context.Logger.LogInformation("TLS secret '{SecretName}' already exists, skipping bootstrap.", secretName);
                    continue;
                }
            }

            context.Logger.LogInformation("Creating bootstrap TLS secret '{SecretName}' for '{Hostname}'.", secretName, hostname);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={hostname}", ecdsa, HashAlgorithmName.SHA256);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostname);
            request.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

            var certPem = cert.ExportCertificatePem();
            var keyPem = ecdsa.ExportECPrivateKeyPem();

            var tempDir = Directory.CreateTempSubdirectory(".aspire-tls-bootstrap");
            try
            {
                var certPath = Path.Combine(tempDir.FullName, "tls.crt");
                var keyPath = Path.Combine(tempDir.FullName, "tls.key");
                await File.WriteAllTextAsync(certPath, certPem, context.CancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(keyPath, keyPem, context.CancellationToken).ConfigureAwait(false);

                var createArgs = $"create secret tls {secretName} --cert=\"{certPath}\" --key=\"{keyPath}\" --namespace {@namespace}";
                if (environment.KubeConfigPath is not null)
                {
                    createArgs += $" --kubeconfig \"{environment.KubeConfigPath}\"";
                }

                var (createResult, createDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
                {
                    Arguments = createArgs,
                    ThrowOnNonZeroReturnCode = false,
                    InheritEnv = true,
                    OnOutputData = line => context.Logger.LogDebug("{Line}", line),
                    OnErrorData = line => context.Logger.LogDebug("{Line}", line)
                });

                await using (createDisposable.ConfigureAwait(false))
                {
                    var createExitResult = await createResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    if (createExitResult.ExitCode != 0)
                    {
                        context.Logger.LogWarning("Failed to create bootstrap TLS secret '{SecretName}' (exit code {ExitCode}).", secretName, createExitResult.ExitCode);
                    }
                    else
                    {
                        context.Logger.LogInformation("Bootstrap TLS secret '{SecretName}' created for '{Hostname}'. Replace with a real cert via cert-manager or manually.", secretName, hostname);
                    }
                }
            }
            finally
            {
                DeleteTempDirSafely(tempDir, context.Logger);
            }
        }
    }
}
