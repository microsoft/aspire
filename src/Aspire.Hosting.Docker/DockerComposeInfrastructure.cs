// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents the infrastructure for Docker Compose within the Aspire Hosting environment.
/// Implements <see cref="IDistributedApplicationEventingSubscriber"/> and subscribes to <see cref="BeforeStartEvent"/> to configure Docker Compose resources before publish.
/// </summary>
internal sealed class DockerComposeInfrastructure(
    ILogger<DockerComposeInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext) : IDistributedApplicationEventingSubscriber
{
    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsRunMode)
        {
            return;
        }

        // Find Docker Compose environment resources
        var dockerComposeEnvironments = @event.Model.Resources.OfType<DockerComposeEnvironmentResource>().ToArray();

        if (dockerComposeEnvironments.Length == 0)
        {
            EnsureNoPublishAsDockerComposeServiceAnnotations(@event.Model);
            return;
        }

        foreach (var environment in dockerComposeEnvironments)
        {
            var dockerComposeEnvironmentContext = new DockerComposeEnvironmentContext(environment, logger);

            if (environment.DashboardEnabled && environment.Dashboard?.Resource is DockerComposeAspireDashboardResource dashboard)
            {
                // Ensure the dashboard resource is created (even though it's not part of the main application model)
                var dashboardService = await dockerComposeEnvironmentContext.CreateDockerComposeServiceResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);

                dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
                {
                    ComputeEnvironment = environment,
                    ContainerRegistry = GetContainerRegistry(environment, @event.Model)
                });
            }

            foreach (var r in @event.Model.GetComputeResources())
            {
                // Configure OTLP for resources if dashboard is enabled (before creating the service resource)
                if (environment.DashboardEnabled && environment.Dashboard?.Resource.OtlpGrpcEndpoint is EndpointReference otlpGrpcEndpoint)
                {
                    ConfigureOtlp(r, otlpGrpcEndpoint);
                }

                // Create a Docker Compose compute resource for the resource
                var serviceResource = await dockerComposeEnvironmentContext.CreateDockerComposeServiceResourceAsync(r, executionContext, cancellationToken).ConfigureAwait(false);

                // Add deployment target annotation to the resource
                r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
                {
                    ComputeEnvironment = environment,
                    ContainerRegistry = GetContainerRegistry(environment, @event.Model)
                });
            }
        }
    }

    private static IContainerRegistry GetContainerRegistry(DockerComposeEnvironmentResource environment, DistributedApplicationModel appModel)
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

        // Fall back to local container registry for Docker Compose scenarios
        return LocalContainerRegistry.Instance;
    }

    private static void EnsureNoPublishAsDockerComposeServiceAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var r in appModel.GetComputeResources())
        {
            if (r.HasAnnotationOfType<DockerComposeServiceCustomizationAnnotation>())
            {
                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as a Docker Compose service, but there are no '{nameof(DockerComposeEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(DockerComposeEnvironmentExtensions.AddDockerComposeEnvironment)}'.");
            }
        }
    }

    private static void ConfigureOtlp(IResource resource, EndpointReference otlpEndpoint)
    {
        // Only configure OTLP for resources that have the OtlpExporterAnnotation and implement IResourceWithEnvironment
        if (resource is IResourceWithEnvironment resourceWithEnv && resource.Annotations.OfType<OtlpExporterAnnotation>().Any())
        {
            // Configure OTLP environment variables
            resourceWithEnv.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";
                context.EnvironmentVariables["OTEL_SERVICE_NAME"] = resource.Name;
                return Task.CompletedTask;
            }));
        }
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
