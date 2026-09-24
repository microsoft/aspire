// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting;

/// <summary>
/// Provides the APIs used to project a resource onto a different runtime shape, such as running or publishing a
/// non-container resource as a container.
/// </summary>
/// <remarks>
/// These extensions deliberately target <see cref="IResource"/> rather than <see cref="ContainerResource"/>: the
/// point of a projection is to give a resource that is not already a container a container shape for one operation.
/// <para>
/// A configuration callback receives a builder whose resource is a distinct typed projection, not the model
/// owner. Use that builder for container-specific configuration and
/// <see cref="ResourceExtensions.GetOwnerOrSelf"/> for identity-sensitive state, model membership, or reference
/// comparisons. Casting the projection to <see cref="IResource"/> does not resolve its owner.
/// Typed resource-event callbacks registered through the projection also retain that typed view.
/// </para>
/// <para>
/// The owner and projection share one live annotation collection. Configuration written through either view is
/// therefore visible through the other; a projection is an active configuration view rather than an isolated
/// snapshot. Projecting a parent does not automatically project its children. Integrations whose local shape uses
/// different child resource types must register those child projections explicitly.
/// </para>
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
    /// <param name="configure">Configuration applied to the projection.</param>
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
    /// The projection is a typed configuration view, not a separately added logical model member. The resource
    /// collection retains the owner as the pair's canonical identity while exposing the projection as its effective
    /// resource for enumeration and typed queries. This preserves a single model slot while references, events, and
    /// notifications remain addressed to the owner.
    /// Extension authors storing resource references in custom annotations or dictionaries should call
    /// <see cref="ResourceExtensions.GetOwnerOrSelf"/> inside <paramref name="configure"/> when those references
    /// represent logical identity. During <paramref name="createProjection"/>, use the original owner directly:
    /// a custom projection is not registered until its factory returns.
    /// </para>
    /// <para>
    /// Nothing is selected and the callback does not run when the AppHost is not performing <paramref name="operation"/>,
    /// so a run-mode projection contributes no configuration to a publish and vice versa.
    /// </para>
    /// <para>
    /// The first projection selected for an operation determines the permanent projection type. Repeating a typed
    /// projection with the same <typeparamref name="TContainer"/> reuses that instance. A later default projection
    /// configuration can also configure a previously selected custom projection, but a custom projection cannot
    /// replace a default projection or a different custom projection.
    /// </para>
    /// <para>
    /// The callback runs synchronously against the selected projection. An integration nests the caller's callback
    /// inside its own, so its defaults and the caller's configuration are applied together on every call. This
    /// matches the pre-projection <c>RunAsEmulator</c> and <c>PublishAsDockerFile</c> conventions and lets a repeat
    /// call override a value such as the image.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when any required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the owner or projection has a fixed model shape, when the owner is already a
    /// <see cref="ContainerResource"/>, when the projection does not use the owner's name or annotation collection,
    /// or when the resource is already projected as an incompatible container type.
    /// </exception>
    [Experimental("ASPIREPROJECTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Integration authoring primitive — integrations export their own RunAs/PublishAs overloads.")]
    public static IResourceBuilder<T> WithContainerProjection<T, TContainer>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Func<TContainer> createProjection,
        Action<IResourceBuilder<TContainer>> configure)
        where T : IResource
        where TContainer : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createProjection);
        ArgumentNullException.ThrowIfNull(configure);

        if (TryGetProjectionRegistration<T, TContainer>(builder, operation, out var addRegistration) is not { } registration)
        {
            return builder;
        }

        var projection = registration.GetOrCreateCustomProjection(
            createProjection,
            candidate => ValidateProjection(registration.Owner, candidate, "container"),
            "container");
        if (addRegistration)
        {
            registration.Owner.Annotations.Add(registration);
        }

        configure(builder.ApplicationBuilder.CreateResourceBuilder(projection));

        return builder;
    }

    private static ResourceProjectionAnnotation? TryGetProjectionRegistration<T, TProjection>(
        IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        out bool addRegistration)
        where T : IResource
        where TProjection : class, IResource
    {
        addRegistration = false;

        ValidateProjectionCompatibility(builder.Resource, typeof(TProjection));

        if (builder.ApplicationBuilder.ExecutionContext.Operation != operation)
        {
            return null;
        }

        if (builder.Resource.Annotations.OfType<ResourceProjectionAnnotation>().SingleOrDefault() is { } existing)
        {
            return existing;
        }

        addRegistration = true;
        return new ResourceProjectionAnnotation(builder.Resource);
    }

    private static void ValidateProjection(IResource owner, IResource projection, string projectionKind)
    {
        ValidateProjectionCompatibility(owner, projection.GetType());

        // A projection stands in for its owner, so it must share the owner's identity and annotation storage.
        // Validating both here turns an easy authoring mistake into an actionable error instead of a projection
        // that silently drops configuration or competes with the owner for a name.
        if (ReferenceEquals(projection, owner))
        {
            throw new InvalidOperationException(
                $"The {projectionKind} projection for '{owner.Name}' must be a distinct resource instance.");
        }

        if (!string.Equals(projection.Name, owner.Name, StringComparisons.ResourceName))
        {
            throw new InvalidOperationException(
                $"The {projectionKind} projection '{projection.Name}' must use the same name as its owner '{owner.Name}'.");
        }

        if (!ReferenceEquals(projection.Annotations, owner.Annotations))
        {
            throw new InvalidOperationException(
                $"The {projectionKind} projection for '{owner.Name}' must share its owner's annotation collection. " +
                $"Override '{nameof(IResource.Annotations)}' on '{projection.GetType().Name}' to return the owner's annotations.");
        }
    }

    private static void ValidateProjectionCompatibility(IResource owner, Type projectionType)
    {
        if (owner is IResourceWithoutProjections)
        {
            throw new InvalidOperationException(
                $"The resource '{owner.Name}' has a fixed model shape and cannot be projected.");
        }

        if (typeof(IResourceWithoutProjections).IsAssignableFrom(projectionType))
        {
            throw new InvalidOperationException(
                $"The resource type '{projectionType.Name}' has a fixed model shape and cannot be used as a projection.");
        }

        if (owner is ContainerResource &&
            typeof(ContainerResource).IsAssignableFrom(projectionType))
        {
            throw new InvalidOperationException(
                $"The container resource '{owner.Name}' cannot be projected to container type '{projectionType.Name}'. " +
                "Configure the container directly or project it to a different resource shape.");
        }

        if (IsExecutableShape(owner.GetType()) &&
            IsExecutableShape(projectionType))
        {
            throw new InvalidOperationException(
                $"The executable resource '{owner.Name}' cannot be projected to executable type '{projectionType.Name}'. " +
                "Configure the executable directly or project it to a different resource shape.");
        }
    }

    private static bool IsExecutableShape(Type resourceType) =>
        typeof(ExecutableResource).IsAssignableFrom(resourceType) ||
        typeof(ProjectResource).IsAssignableFrom(resourceType);

    /// <summary>
    /// Projects the resource onto another effective resource shape for the specified operation.
    /// </summary>
    /// <typeparam name="T">The owning resource type.</typeparam>
    /// <typeparam name="TProjection">The effective resource type the owner is projected as.</typeparam>
    /// <param name="builder">Builder for the resource being projected.</param>
    /// <param name="operation">The operation the projection applies to.</param>
    /// <param name="createProjection">
    /// Creates the effective resource view. The projection must use the owner's name and return the owner's
    /// <see cref="IResource.Annotations"/> collection.
    /// </param>
    /// <param name="configure">Configuration applied to the effective resource view.</param>
    /// <returns>The <paramref name="builder"/>, so the owner keeps its original type.</returns>
    /// <remarks>
    /// <para>
    /// The projection is not added as another logical model member. The resource collection retains the owner
    /// as canonical identity while exposing the projection for effective resource discovery in the selected operation.
    /// The owner and projection share live annotations, so configuration applied through either view affects both.
    /// </para>
    /// <para>
    /// This method provides projection selection, identity, collection, and capability-resolution behavior. It does
    /// not make every resource type realizable by every runtime or publisher. The integration must select a projection
    /// type supported by the consumers involved in that operation and must register projections for child resources
    /// whose effective types also change.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when any required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the owner or projection has a fixed model shape, when the source and target shapes are
    /// incompatible, when the projection is the owner, does not use the owner's name or annotations, or conflicts
    /// with a projection type already selected for the active operation.
    /// </exception>
    [Experimental("ASPIREPROJECTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Integration authoring primitive — integrations export their own operation-specific overloads.")]
    public static IResourceBuilder<T> WithResourceProjection<T, TProjection>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Func<TProjection> createProjection,
        Action<IResourceBuilder<TProjection>> configure)
        where T : IResource
        where TProjection : class, IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createProjection);
        ArgumentNullException.ThrowIfNull(configure);

        if (TryGetProjectionRegistration<T, TProjection>(builder, operation, out var addRegistration) is not { } registration)
        {
            return builder;
        }

        var projection = registration.GetOrCreateCustomProjection(
            createProjection,
            candidate => ValidateProjection(registration.Owner, candidate, "resource"),
            "resource");
        if (addRegistration)
        {
            registration.Owner.Annotations.Add(registration);
        }

        configure(builder.ApplicationBuilder.CreateResourceBuilder(projection));

        return builder;
    }

    /// <summary>
    /// Projects the resource onto a plain <see cref="ContainerResource"/> for the specified operation.
    /// </summary>
    /// <remarks>
    /// Used by the built-in <c>PublishAsDockerFile</c> paths, where the projection has no integration-specific type.
    /// </remarks>
    internal static IResourceBuilder<T> WithContainerProjection<T>(
        this IResourceBuilder<T> builder,
        DistributedApplicationOperation operation,
        Action<IResourceBuilder<ContainerResource>> configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (TryGetProjectionRegistration<T, ContainerResource>(builder, operation, out var addRegistration) is not { } registration)
        {
            return builder;
        }

        var projection = registration.GetOrCreateDefaultProjection(
            () => (ContainerResource)new ContainerResourceProjection<IResource>(registration.Owner),
            candidate => ValidateProjection(registration.Owner, candidate, "container"));
        if (addRegistration)
        {
            registration.Owner.Annotations.Add(registration);
        }

        configure(builder.ApplicationBuilder.CreateResourceBuilder(projection));

        return builder;
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
    /// The callback's resource is a distinct container view. Use <see cref="ResourceExtensions.GetOwnerOrSelf"/>
    /// when capturing its logical identity rather than its container-specific configuration.
    /// </remarks>
    // Hidden from container resources in the generated SDKs. Polyglot callers have no analyzer, so without
    // this the method would be offered on every container type and only fail at run time.
    [AspireExport(RunSyncOnBackgroundThread = true, ExcludeTargetTypes = [typeof(ContainerResource), typeof(IResourceWithoutProjections)])]
    public static IResourceBuilder<T> RunAsContainerImage<T>(this IResourceBuilder<T> builder, string image, Action<IResourceBuilder<ContainerResource>>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        return builder.WithContainerProjection(
            DistributedApplicationOperation.Run,
            // The image is applied ahead of the caller's configuration so a repeat call overrides the previous
            // image, and so an explicit WithImage inside the callback still takes precedence over it.
            container =>
            {
                container.WithImageReference(image);
                configure?.Invoke(container);
            });
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
    /// Use <see cref="ResourceExtensions.GetOwnerOrSelf"/> in the callback to obtain the logical model identity
    /// without assuming that the owner has the container's CLR type.
    /// </para>
    /// <para>
    /// An integration's <c>RunAsContainer</c> nests the caller's callback inside its own, so a single callback is
    /// enough here: this method contributes only the image, and everything an integration considers a default is
    /// just the first part of the callback it passes.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREPROJECTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
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
            container =>
            {
                container.WithImageReference(image);
                configure?.Invoke(container);
            });
    }

}
