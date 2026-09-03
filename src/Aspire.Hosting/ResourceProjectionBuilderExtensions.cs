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

        if (builder.ApplicationBuilder.ExecutionContext.Operation != operation)
        {
            return builder;
        }

        if (builder.Resource.TryGetAppliedContainerProjection(out var existingProjection))
        {
            var existingProjectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder(existingProjection);
            configure(existingProjectionBuilder);
            return builder;
        }

        // The projection is a typed configuration view, not a logical model member. Register it
        // before invoking the callback so builder lookups made during configuration can resolve it.
        var projection = new ContainerResourceProjection<T>(builder.Resource);
        builder.Resource.Annotations.Add(new ContainerResourceProjectionAnnotation(projection));
        configure(builder.ApplicationBuilder.CreateResourceBuilder<ContainerResource>(projection));

        return builder;
    }
}
