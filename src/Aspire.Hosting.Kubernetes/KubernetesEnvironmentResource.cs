// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public string HelmChartName { get; set; } = "aspire";

    /// <summary>
    /// Gets or sets the version of the Helm chart to be generated.
    /// This property specifies the version number that will be assigned to the Helm chart,
    /// typically following semantic versioning conventions.
    /// </summary>
    public string HelmChartVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Gets or sets the description of the Helm chart being generated.
    /// </summary>
    public string HelmChartDescription { get; set; } = "Aspire Helm Chart";

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
            publishStep.RequiredBy(WellKnownPipelineSteps.Publish);
            steps.Add(publishStep);

            // Deployment engine steps (e.g., Helm prepare, deploy, uninstall)
            if (environment.DeploymentEngineStepsFactory is not null)
            {
                var engineSteps = await environment.DeploymentEngineStepsFactory(environment, factoryContext).ConfigureAwait(false);
                steps.AddRange(engineSteps);
            }

            // Expand deployment target steps for compute resources (including dashboard if enabled)
            var resources = environment.DashboardEnabled && environment.Dashboard?.Resource is KubernetesAspireDashboardResource dashboard
                ? [.. model.GetComputeResources(), dashboard]
                : model.GetComputeResources();

            foreach (var computeResource in resources)
            {
                var targetEnv = (IComputeEnvironmentResource?)environment.OwningComputeEnvironment ?? environment;
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
                var targetEnv = (IComputeEnvironmentResource?)OwningComputeEnvironment ?? this;
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
                var helmDeploySteps = context.GetSteps(this, "helm-deploy");
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

        // Create a Kubernetes resource for the dashboard if enabled
        if (DashboardEnabled && Dashboard?.Resource is KubernetesAspireDashboardResource dashboard)
        {
            var dashboardService = await environmentContext.CreateKubernetesResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);
            dashboardService.AddPrintSummaryStep();

            dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
            {
                ComputeEnvironment = this,
                ContainerRegistry = containerRegistry
            });
        }

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
            var computeEnvForAnnotation = resourceComputeEnvironment ?? (IComputeEnvironmentResource)this;
            r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
            {
                ComputeEnvironment = computeEnvForAnnotation,
                ContainerRegistry = containerRegistry
            });
        }
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
}
