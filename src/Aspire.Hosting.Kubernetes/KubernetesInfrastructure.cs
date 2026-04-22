// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents the infrastructure for Kubernetes within the Aspire Hosting environment.
/// Implements <see cref="IDistributedApplicationEventingSubscriber"/> and subscribes to <see cref="BeforeStartEvent"/> to configure Kubernetes resources before publish.
/// </summary>
internal sealed class KubernetesInfrastructure(
    ILogger<KubernetesInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext) : IDistributedApplicationEventingSubscriber
{
    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsRunMode)
        {
            return;
        }

        // Find Kubernetes environment resources
        var kubernetesEnvironments = @event.Model.Resources.OfType<KubernetesEnvironmentResource>().ToArray();

        if (kubernetesEnvironments.Length == 0)
        {
            EnsureNoPublishAsKubernetesServiceAnnotations(@event.Model);
            return;
        }

        foreach (var environment in kubernetesEnvironments)
        {
            var environmentContext = new KubernetesEnvironmentContext(environment, logger);
            var containerRegistry = GetContainerRegistry(environment, @event.Model);

            // Create a Kubernetes resource for the dashboard if enabled
            if (environment.DashboardEnabled && environment.Dashboard?.Resource is KubernetesAspireDashboardResource dashboard)
            {
                var dashboardService = await environmentContext.CreateKubernetesResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);
                dashboardService.AddPrintSummaryStep();

                dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
                {
                    ComputeEnvironment = environment,
                    ContainerRegistry = containerRegistry
                });
            }

            foreach (var r in @event.Model.GetComputeResources())
            {
                // Skip resources that are explicitly targeted to a different compute environment.
                // Also match if the resource targets a parent compute environment (e.g., AKS)
                // that owns this Kubernetes environment.
                var resourceComputeEnvironment = r.GetComputeEnvironment();
                if (resourceComputeEnvironment is not null &&
                    resourceComputeEnvironment != environment &&
                    resourceComputeEnvironment != environment.OwningComputeEnvironment)
                {
                    continue;
                }

                // Configure OTLP for resources if dashboard is enabled
                if (environment.DashboardEnabled && environment.Dashboard?.Resource.OtlpGrpcEndpoint is EndpointReference otlpGrpcEndpoint)
                {
                    ConfigureOtlp(r, otlpGrpcEndpoint);
                }

                // Create a Kubernetes compute resource for the resource
                var serviceResource = await environmentContext.CreateKubernetesResourceAsync(r, executionContext, cancellationToken).ConfigureAwait(false);
                serviceResource.AddPrintSummaryStep();

                // Configure ingress for resources with external HTTP endpoints.
                await ConfigureIngressAsync(r, serviceResource, environment).ConfigureAwait(false);

                // Add deployment target annotation to the resource.
                // Use the resource's actual compute environment (which may be a parent
                // like AzureKubernetesEnvironmentResource) so that GetDeploymentTargetAnnotation
                // can match it correctly during publish.
                var computeEnvForAnnotation = resourceComputeEnvironment ?? (IComputeEnvironmentResource)environment;
                r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
                {
                    ComputeEnvironment = computeEnvForAnnotation,
                    ContainerRegistry = containerRegistry
                });
            }

            // Add pipeline steps for any Helm charts to be installed into the cluster.
            // These run after credentials are fetched and before the app Helm chart deploy.
            AddHelmChartInstallSteps(environment);
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

    private static void EnsureNoPublishAsKubernetesServiceAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var r in appModel.GetComputeResources())
        {
            if (r.HasAnnotationOfType<KubernetesServiceCustomizationAnnotation>())
            {
                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as a Kubernetes service, but there are no '{nameof(KubernetesEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(KubernetesEnvironmentExtensions.AddKubernetesEnvironment)}'.");
            }
        }
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

    private static void AddHelmChartInstallSteps(KubernetesEnvironmentResource environment)
    {
        if (!environment.TryGetAnnotationsOfType<HelmChartAnnotation>(out var chartAnnotations))
        {
            return;
        }

        foreach (var chart in chartAnnotations)
        {
            environment.Annotations.Add(new PipelineStepAnnotation((_) =>
            {
                var step = new PipelineStep
                {
                    Name = $"helm-install-{chart.ReleaseName}",
                    Description = $"Installs Helm chart '{chart.ReleaseName}'",
                    Action = ctx => InstallHelmChartAsync(ctx, environment, chart)
                };

                // Must run before the app's Helm prepare step.
                step.RequiredBy($"prepare-{environment.Name}");

                // For AKS environments, depend on the credentials step so we have
                // a valid kubeconfig. The credentials step name follows the pattern
                // 'aks-get-credentials-{owningEnvironmentName}'.
                if (environment.OwningComputeEnvironment is { } owning)
                {
                    step.DependsOn($"aks-get-credentials-{owning.Name}");
                }

                return new[] { step };
            }));
        }
    }

    private static async Task InstallHelmChartAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        HelmChartAnnotation chart)
    {
        var helmRunner = context.Services.GetRequiredService<IHelmRunner>();
        var options = chart.Options;

        var args = $"upgrade {chart.ReleaseName} {options.Chart} --install";

        if (!string.IsNullOrEmpty(options.Version))
        {
            args += $" --version {options.Version}";
        }

        if (!string.IsNullOrEmpty(options.Namespace))
        {
            args += $" --namespace {options.Namespace}";
        }

        if (options.CreateNamespace)
        {
            args += " --create-namespace";
        }

        foreach (var (key, value) in options.Values)
        {
            args += $" --set {key}={value}";
        }

        if (!string.IsNullOrEmpty(environment.KubeConfigPath))
        {
            args += $" --kubeconfig \"{environment.KubeConfigPath}\"";
        }

        if (options.Wait)
        {
            args += " --wait";
        }

        args += $" --timeout {options.Timeout.TotalSeconds}s";

        context.Logger.LogInformation("Installing Helm chart: helm {Args}", args);

        var exitCode = await helmRunner.RunAsync(
            args,
            workingDirectory: null,
            onOutputData: line => context.Logger.LogDebug("{Line}", line),
            onErrorData: line => context.Logger.LogDebug("{Line}", line),
            cancellationToken: context.CancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Helm chart '{chart.ReleaseName}' installation failed (exit code {exitCode})");
        }
    }

    private static async Task ConfigureIngressAsync(
        IResource resource,
        KubernetesResource kubernetesResource,
        KubernetesEnvironmentResource environment)
    {
        // Collect external HTTP/HTTPS endpoints from the resource's endpoint annotations.
        var externalHttpEndpoints = resource.Annotations
            .OfType<EndpointAnnotation>()
            .Where(e => e.IsExternal && e.UriScheme is "http" or "https")
            .ToList();

        if (externalHttpEndpoints.Count == 0)
        {
            return;
        }

        // Check for a resource-level ingress annotation first, then fall back to environment-level.
        if (!resource.TryGetLastAnnotation<KubernetesIngressConfigurationAnnotation>(out var ingressAnnotation))
        {
            environment.TryGetLastAnnotation<KubernetesIngressConfigurationAnnotation>(out ingressAnnotation);
        }

        if (ingressAnnotation is null)
        {
            return;
        }

        var context = new KubernetesIngressContext
        {
            KubernetesResource = kubernetesResource,
            Resource = resource,
            Environment = environment,
            ExternalHttpEndpoints = externalHttpEndpoints
        };

        await ingressAnnotation.Configure(context).ConfigureAwait(false);
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
