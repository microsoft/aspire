// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class ResourceProjectionBuilderExtensions
{
    [AspireExportIgnore(Reason = "Internal resource projection implementation.")]
    public static IResourceBuilder<T> WithContainerProjection<T>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Action<IResourceBuilder<ContainerResource>> configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var executionContext = builder.ApplicationBuilder.ExecutionContext;
        if (executionContext.Operation != operation)
        {
            return builder;
        }

        // Reuse the shared selection helper so registering a second projection and resolving the
        // effective resource fail the same way instead of one silently picking the first match.
        if (builder.Resource.TrySelectProjection(executionContext, out var selectedProjection))
        {
            if (selectedProjection is not ContainerResource containerProjection)
            {
                throw new DistributedApplicationException(
                    $"Resource '{builder.Resource.Name}' already has a non-container projection selected for the '{operation}' operation.");
            }

            containerProjection.Annotations.MaterializeInheritedAnnotations<EndpointAnnotation>(
                CloneEndpoint,
                endpoint => endpoint.Name);

            var existingProjectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder(containerProjection);
            containerProjection.Annotations.ConfigureProjection(() => configure(existingProjectionBuilder));
            return builder;
        }

        // A projection is configured through a builder but is deliberately not added to Resources.
        // The owner remains the only model member so references, events, and notifications keep
        // using the canonical object identity established before projection registration.
        var projection = new ContainerResourceProjection<T>(builder.Resource);
        var projectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder<ContainerResource>(projection);

        projection.Annotations.MaterializeInheritedAnnotations<EndpointAnnotation>(
            CloneEndpoint,
            endpoint => endpoint.Name);

        // Container image annotations discriminate the effective shape. A container projection
        // must define its own image without exposing or mutating an owner-level legacy discriminator.
        projection.Annotations.SuppressInheritedAnnotations<ContainerImageAnnotation>();
        // Hide callbacks present at registration because they describe the owner's legacy shape.
        // Later callbacks remain visible so APIs such as ExcludeFromManifest retain call ordering.
        projection.Annotations.RemoveAnnotations<ManifestPublishingCallbackAnnotation>();

        // Configuration callbacks must never observe mutable owner annotation instances. Known
        // annotations that support in-place container configuration are materialized above; all
        // other inherited annotations remain hidden until configuration completes.
        projection.Annotations.ConfigureProjection(() => configure(projectionBuilder));

        builder.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(operation, projection)));

        return builder;
    }

    private static EndpointAnnotation CloneEndpoint(EndpointAnnotation endpoint)
    {
        // Projection registration happens while building the model, before endpoint allocation.
        // Clone model-time endpoint configuration so container callbacks can adapt the selected
        // shape without changing endpoint semantics on the canonical owner.
        var clone = new EndpointAnnotation(
            endpoint.Protocol,
            endpoint.DefaultNetworkID,
            endpoint.UriScheme,
            endpoint.Transport,
            endpoint.Name,
            endpoint.SpecifiedPort,
            endpoint.TargetPort,
            endpoint.IsExternal,
            endpoint.IsExplicitlyProxied)
        {
            ExcludeReferenceEndpoint = endpoint.ExcludeReferenceEndpoint,
            FromLaunchProfile = endpoint.FromLaunchProfile,
            TargetHost = endpoint.TargetHost,
            TargetPortEnvironmentVariable = endpoint.TargetPortEnvironmentVariable,
            TlsEnabled = endpoint.TlsEnabled
        };

        // Endpoint references retain the owner, so both shapes must complete the same allocation
        // snapshots even though model-time endpoint configuration is independently mutable.
        clone.ShareAllocationStateWith(endpoint);
        return clone;
    }
}
