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

        foreach (var annotation in builder.Resource.Annotations.OfType<ResourceProjectionAnnotation>())
        {
            if (!annotation.Source.TrySelect(executionContext, out var selectedProjection))
            {
                continue;
            }

            if (selectedProjection is not ContainerResource containerProjection)
            {
                throw new DistributedApplicationException(
                    $"Resource '{builder.Resource.Name}' already has a non-container projection selected for the '{operation}' operation.");
            }

            configure(builder.ApplicationBuilder.CreateResourceBuilder(containerProjection));
            return builder;
        }

        // A projection is configured through a builder but is deliberately not added to Resources.
        // The owner remains the only model member so references, events, and notifications keep
        // using the canonical object identity established before projection registration.
        var projection = new ContainerResourceProjection<T>(builder.Resource);
        var projectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder<ContainerResource>(projection);

        // Container image annotations discriminate the effective shape and are mutable. Remove
        // inherited instances before invoking container APIs so WithImage cannot mutate an owner
        // annotation in place and the selected projection remains authoritative.
        foreach (var imageAnnotation in projection.Annotations.OfType<ContainerImageAnnotation>().ToArray())
        {
            projection.Annotations.Remove(imageAnnotation);
        }

        configure(projectionBuilder);

        builder.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(operation, projection)));

        return builder;
    }
}
