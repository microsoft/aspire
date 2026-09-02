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

            var existingProjectionAnnotation = builder.Resource.Annotations
                .OfType<ContainerResourceProjectionAnnotation>()
                .Single();
            var existingProjectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder(containerProjection);
            configure(existingProjectionBuilder);
            SynchronizeProperties(containerProjection, existingProjectionAnnotation);
            return builder;
        }

        // A projection is configured through a builder but is deliberately not added to Resources.
        // The owner remains the only model member so references, events, and notifications keep
        // using the canonical object identity established before projection registration.
        var projection = new ContainerResourceProjection<T>(builder.Resource);
        var projectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder<ContainerResource>(projection);
        var projectionAnnotation = new ContainerResourceProjectionAnnotation();
        var sourceAnnotation = new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(operation, projection));

        // Register before configuration so APIs invoked by the callback can resolve the typed view
        // by name. The projection itself is never added to the logical resource collection.
        builder.Resource.Annotations.Add(projectionAnnotation);
        builder.Resource.Annotations.Add(sourceAnnotation);
        configure(projectionBuilder);
        SynchronizeProperties(projection, projectionAnnotation);

        return builder;
    }

    private static void SynchronizeProperties(
        ContainerResource projection,
        ContainerResourceProjectionAnnotation annotation)
    {
        // Most container configuration is annotation-based and therefore writes directly to the
        // owner. Preserve the two legacy ContainerResource properties in the same owner-side model.
        annotation.Entrypoint = projection.Entrypoint;
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001
        annotation.ShellExecution = projection.ShellExecution;
#pragma warning restore ASPIRECONTAINERSHELLEXECUTION001
    }
}
