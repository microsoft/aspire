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
        var source = builder.Resource.Annotations
            .OfType<ResourceProjectionAnnotation>()
            .Select(static annotation => annotation.Source)
            .OfType<IContainerResourceProjectionSource>()
            .SingleOrDefault(source => source.Operation == operation);

        if (source is not null)
        {
            source.Configure(configure);
            return builder;
        }

        if (executionContext.Operation == operation &&
            builder.Resource.TrySelectProjection(executionContext, out var selectedProjection))
        {
            if (selectedProjection is not ContainerResource containerProjection)
            {
                throw new DistributedApplicationException(
                    $"Resource '{builder.Resource.Name}' already has a non-container projection selected for the '{operation}' operation.");
            }

            var existingProjectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder(containerProjection);
            configure(existingProjectionBuilder);
            return builder;
        }

        source = new ContainerResourceProjectionSource<T>(builder, operation);
        source.Configure(configure);
        builder.Resource.Annotations.Add(new ResourceProjectionAnnotation(source));

        // Selection realizes only the source for this AppHost invocation. Registrations for other
        // operations remain inert and do not add an effective-shape marker to the owner.
        builder.Resource.TrySelectProjection(executionContext, out _);

        return builder;
    }

    private interface IContainerResourceProjectionSource : IResourceProjectionSource
    {
        DistributedApplicationOperation Operation { get; }

        void Configure(Action<IResourceBuilder<ContainerResource>> configure);
    }

    private sealed class ContainerResourceProjectionSource<T>(
        IResourceBuilder<T> ownerBuilder,
        DistributedApplicationOperation operation) : IContainerResourceProjectionSource
        where T : IResource
    {
        private readonly List<Action<IResourceBuilder<ContainerResource>>> _configurations = [];
        private ContainerResource? _projection;

        public DistributedApplicationOperation Operation { get; } = operation;

        public void Configure(Action<IResourceBuilder<ContainerResource>> configure)
        {
            if (_projection is not null)
            {
                configure(ownerBuilder.ApplicationBuilder.CreateResourceBuilder(_projection));
                return;
            }

            _configurations.Add(configure);
        }

        public bool TrySelect(
            DistributedApplicationExecutionContext executionContext,
            out IResource? projection)
        {
            if (executionContext.Operation != Operation)
            {
                projection = null;
                return false;
            }

            if (_projection is null)
            {
                // The projection is a typed configuration view, not a logical model member. Assign it
                // before invoking callbacks so builder lookups made during configuration can resolve
                // the selected view without recursively realizing it.
                _projection = new ContainerResourceProjection<T>(ownerBuilder.Resource);
                ownerBuilder.Resource.Annotations.Add(new ContainerResourceProjectionAnnotation(_projection));

                var projectionBuilder = ownerBuilder.ApplicationBuilder.CreateResourceBuilder<ContainerResource>(_projection);
                var configurations = _configurations.ToArray();
                _configurations.Clear();

                foreach (var configure in configurations)
                {
                    configure(projectionBuilder);
                }
            }

            projection = _projection;
            return true;
        }
    }
}
