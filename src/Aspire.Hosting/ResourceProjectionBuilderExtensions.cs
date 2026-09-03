// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides the APIs used to project a resource onto a different runtime shape, such as running or publishing a
/// non-container resource as a container.
/// </summary>
/// <remarks>
/// These extensions deliberately target <see cref="IResource"/> rather than <see cref="ContainerResource"/>: the
/// point of a projection is to give a resource that is not already a container a container shape for one operation.
/// </remarks>
public static class ResourceProjectionBuilderExtensions
{
    /// <summary>
    /// Projects the resource onto a container for the specified operation.
    /// </summary>
    /// <typeparam name="T">The owning resource type.</typeparam>
    /// <typeparam name="TContainer">The container type the owner is projected as.</typeparam>
    /// <param name="builder">Builder for the resource being projected.</param>
    /// <param name="operation">The operation the projection applies to.</param>
    /// <param name="createProjection">
    /// Creates the container the owner is projected as. The container must use the owner's name and return the
    /// owner's <see cref="IResource.Annotations"/> collection.
    /// </param>
    /// <param name="configureDefaults">The integration's defaults for the projection.</param>
    /// <param name="configure">Caller-supplied configuration applied on top of the defaults.</param>
    /// <returns>The <paramref name="builder"/>, so the owner keeps its original type.</returns>
    /// <remarks>
    /// <para>
    /// This is the authoring primitive behind the <c>RunAsContainer</c> and <c>PublishAsDockerFile</c> conventions.
    /// An integration exposes its own overload on its concrete resource builder type, supplying the container the
    /// resource is projected as together with that integration's defaults, and forwards the caller's configuration
    /// callback. Extension methods cannot be virtual, so the convention comes from each integration declaring an
    /// overload for its own type rather than from overriding a shared implementation.
    /// </para>
    /// <para>
    /// The projection is a typed configuration view, not a logical model member, so it is never added to
    /// <see cref="IDistributedApplicationBuilder.Resources"/>; the owner remains the only member representing the
    /// pair. That keeps references, events, and notifications addressed to a single canonical identity.
    /// </para>
    /// <para>
    /// Nothing is created and neither callback runs when the AppHost is not performing <paramref name="operation"/>,
    /// so a run-mode projection contributes no configuration to a publish and vice versa.
    /// </para>
    /// <para>
    /// Projecting the same resource again reconfigures the projection that already exists instead of replacing it:
    /// <paramref name="createProjection"/> and <paramref name="configureDefaults"/> run only for the first
    /// projection, while <paramref name="configure"/> runs every time. Defaults are applied once because they
    /// commonly add named endpoints, which cannot be added twice.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when any required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="builder"/> is already a <see cref="ContainerResource"/>, when the projection does
    /// not use the owner's name, when it does not share the owner's annotation collection, or when the resource is
    /// already projected as an incompatible container type.
    /// </exception>
    [AspireExportIgnore(Reason = "Integration authoring primitive — integrations export their own RunAs/PublishAs overloads.")]
    public static IResourceBuilder<T> WithContainerProjection<T, TContainer>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Func<TContainer> createProjection,
        Action<IResourceBuilder<TContainer>> configureDefaults,
        Action<IResourceBuilder<TContainer>>? configure)
        where T : IResource
        where TContainer : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createProjection);
        ArgumentNullException.ThrowIfNull(configureDefaults);

        // C# cannot express "T is not a ContainerResource", so the constraint is enforced here. Projecting a
        // container onto a container is always an authoring mistake: the projection shares the owner's annotation
        // collection, so its image and endpoints would collide with the ones the owner already has. This is checked
        // before the operation gate so the mistake surfaces in both run and publish rather than only in one of them.
        if (builder.Resource is ContainerResource)
        {
            throw new InvalidOperationException(
                $"The resource '{builder.Resource.Name}' is already a container and cannot be projected as one. " +
                $"Configure it directly instead.");
        }

        if (builder.ApplicationBuilder.ExecutionContext.Operation != operation)
        {
            return builder;
        }

        var owner = builder.Resource;

        if (owner.Annotations.OfType<ContainerResourceProjectionAnnotation>().SingleOrDefault() is { } existing)
        {
            if (existing.Projection is not TContainer existingProjection)
            {
                throw new InvalidOperationException(
                    $"The resource '{owner.Name}' is already projected as '{existing.Projection.GetType().Name}' " +
                    $"and cannot also be projected as '{typeof(TContainer).Name}'.");
            }

            if (configure is not null)
            {
                existing.Configure(() => configure(builder.ApplicationBuilder.CreateResourceBuilder(existingProjection)));
            }

            return builder;
        }

        var projection = createProjection();

        // A projection stands in for its owner, so it must share the owner's identity and annotation storage.
        // Validating both here turns an easy authoring mistake into an actionable error instead of a projection
        // that silently drops configuration or competes with the owner for a name.
        if (!string.Equals(projection.Name, owner.Name, StringComparisons.ResourceName))
        {
            throw new InvalidOperationException(
                $"The container projection '{projection.Name}' must use the same name as its owner '{owner.Name}'.");
        }

        if (!ReferenceEquals(projection.Annotations, owner.Annotations))
        {
            throw new InvalidOperationException(
                $"The container projection for '{owner.Name}' must share its owner's annotation collection. " +
                $"Override '{nameof(IResource.Annotations)}' on '{projection.GetType().Name}' to return the owner's annotations.");
        }

        var annotation = new ContainerResourceProjectionAnnotation(owner, projection);

        // Register before any configuration runs so lookups performed by the callbacks resolve the projection.
        owner.Annotations.Add(annotation);

        var projectionBuilder = builder.ApplicationBuilder.CreateResourceBuilder(projection);

        annotation.Configure(() => configureDefaults(projectionBuilder));

        if (configure is not null)
        {
            annotation.Configure(() => configure(projectionBuilder));
        }

        return builder;
    }

    /// <summary>
    /// Projects the resource onto a plain <see cref="ContainerResource"/> for the specified operation.
    /// </summary>
    internal static IResourceBuilder<T> WithContainerProjection<T>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Action<IResourceBuilder<ContainerResource>> configureDefaults)
        where T : IResource
        => builder.WithContainerProjection(operation, configureDefaults, configure: null);

    /// <summary>
    /// Projects the resource onto a plain <see cref="ContainerResource"/> for the specified operation.
    /// </summary>
    /// <remarks>
    /// Used by the built-in <c>PublishAsDockerFile</c> paths, where the projection has no integration-specific type.
    /// </remarks>
    internal static IResourceBuilder<T> WithContainerProjection<T>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Action<IResourceBuilder<ContainerResource>> configureDefaults,
        Action<IResourceBuilder<ContainerResource>>? configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithContainerProjection(
            operation,
            () => new ContainerResourceProjection<T>(builder.Resource),
            configureDefaults,
            configure);
    }

    /// <summary>
    /// Runs the resource as a container built from a prebuilt image, leaving how it is published unchanged.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="image">The container image reference, for example <c>contoso/worker:dev</c> or <c>mcr.microsoft.com/dotnet/aspnet:10.0</c>. The registry, image, and tag or digest are recorded separately.</param>
    /// <param name="configure">Optional configuration applied to the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// The image is required so a projection can never exist without a valid container source. Configuration
    /// written inside <paramref name="configure"/> applies only to the run-mode container; configuration written
    /// on <paramref name="builder"/> applies to the resource itself and is seen by every projection of it.
    /// </remarks>
    // Hidden from container resources in the generated SDKs. Polyglot callers have no analyzer, so without
    // this the method would be offered on every container type and only fail at run time.
    [AspireExport(RunSyncOnBackgroundThread = true, ExcludeTargetTypes = [typeof(ContainerResource)])]
    public static IResourceBuilder<T> RunAsContainerImage<T>(this IResourceBuilder<T> builder, string image, Action<IResourceBuilder<ContainerResource>>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        return builder.WithContainerProjection(
            DistributedApplicationOperation.Run,
            // The image identifies the container source, so it belongs to creating the projection and is applied
            // once. The callback carries per-call configuration and runs every time.
            container => container.WithImageReference(image),
            configure);
    }

    /// <summary>
    /// Publishes the resource as a container built from a prebuilt image, leaving how it runs locally unchanged.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="image">The container image reference, for example <c>contoso/service:latest</c> or <c>mcr.microsoft.com/dotnet/aspnet:10.0</c>. The registry, image, and tag or digest are recorded separately.</param>
    /// <param name="configure">Optional configuration applied to the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// The image is required so a projection can never exist without a valid container source. Configuration
    /// written inside <paramref name="configure"/> applies only to the published container; configuration written
    /// on <paramref name="builder"/> applies to the resource itself and is seen by every projection of it.
    /// </remarks>
    // Hidden from container resources in the generated SDKs. Polyglot callers have no analyzer, so without
    // this the method would be offered on every container type and only fail at run time.
    [AspireExport(RunSyncOnBackgroundThread = true, ExcludeTargetTypes = [typeof(ContainerResource)])]
    public static IResourceBuilder<T> PublishAsContainerImage<T>(this IResourceBuilder<T> builder, string image, Action<IResourceBuilder<ContainerResource>>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        return builder.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container.WithImageReference(image),
            configure);
    }

    /// <summary>
    /// Runs the resource as a container of type <typeparamref name="TContainer"/> built from a prebuilt image.
    /// </summary>
    /// <typeparam name="T">The owning resource type.</typeparam>
    /// <typeparam name="TContainer">The container type the owner runs as.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="image">The container image reference, including its registry when it is not the default one.</param>
    /// <param name="configure">Optional configuration applied to the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// This is the overload integrations use to implement their own <c>RunAsContainer</c> convention when the
    /// container has an integration-specific type, such as the Azure emulator resources. The owner keeps its own
    /// type so the call can be chained, and the integration's container type is what reaches the callback.
    /// </para>
    /// <para>
    /// An integration's <c>RunAsContainer</c> nests the caller's callback inside its own, so a single callback is
    /// enough here: this method contributes only the image, and everything an integration considers a default is
    /// just the first part of the callback it passes.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Integration authoring primitive — integrations export their own RunAsContainer overloads.")]
    public static IResourceBuilder<T> RunAsContainerImage<T, TContainer>(
        this IResourceBuilder<T> builder,
        string image,
        Action<IResourceBuilder<TContainer>>? configure = null)
        where T : IResource
        where TContainer : ContainerResource, IContainerProjection<T, TContainer>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        return builder.WithContainerProjection(
            DistributedApplicationOperation.Run,
            // The projection type declares how it is built from its owner, so no factory has to be passed in.
            // Dispatch is static, keeping model building free of reflection and safe to trim.
            () => TContainer.CreateProjection(builder.Resource),
            container => container.WithImageReference(image),
            configure);
    }

    /// <summary>
    /// Publishes the resource as a container of type <typeparamref name="TContainer"/> built from a prebuilt image.
    /// </summary>
    /// <typeparam name="T">The owning resource type.</typeparam>
    /// <typeparam name="TContainer">The container type the owner is published as.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="image">The container image reference, including its registry when it is not the default one.</param>
    /// <param name="configure">Optional configuration applied to the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This is the overload integrations use to implement their own <c>PublishAsContainer</c> convention when the
    /// container has an integration-specific type. The owner keeps its own type so the call can be chained, and the
    /// integration's container type is what reaches the callback.
    /// </remarks>
    [AspireExportIgnore(Reason = "Integration authoring primitive — integrations export their own PublishAsContainer overloads.")]
    public static IResourceBuilder<T> PublishAsContainerImage<T, TContainer>(
        this IResourceBuilder<T> builder,
        string image,
        Action<IResourceBuilder<TContainer>>? configure = null)
        where T : IResource
        where TContainer : ContainerResource, IContainerProjection<T, TContainer>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        return builder.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => TContainer.CreateProjection(builder.Resource),
            container => container.WithImageReference(image),
            configure);
    }
}
