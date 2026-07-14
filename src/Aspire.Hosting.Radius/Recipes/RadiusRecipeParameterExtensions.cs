// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Annotations;

namespace Aspire.Hosting;

/// <summary>
/// Recipe-parameter configuration entry points for
/// <see cref="RadiusEnvironmentResource"/>. These extensions attach a recipe-parameter
/// annotation to the environment that the publisher consumes to emit a <c>parameters</c>
/// block on each recipe entry under <c>Radius.Core/recipePacks</c> (and the legacy
/// <c>Applications.Core/environments</c> inline-recipes shape).
/// </summary>
/// <remarks>
/// <para>
/// Parameter values may be literals (string, number, boolean, array, or string-keyed
/// object), an <see cref="IResourceBuilder{ParameterResource}"/> binding, or a
/// <see cref="Radius.RadiusProviderReference"/> (e.g. <c>RadiusProviderReference.AwsRegion</c>).
/// </para>
/// <para>
/// <b>Precedence</b>: a resource-type-scoped parameter overrides an environment-wide
/// parameter of the same key for recipe entries of that type. Keys that do not collide are
/// merged (union). Repeated calls for the same scope also merge, with the later call winning
/// per key.
/// </para>
/// </remarks>
public static class RadiusRecipeParameterExtensions
{
    /// <summary>
    /// Declares recipe parameters applied to <b>every</b> recipe entry in the
    /// environment's recipe pack.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="configure">
    /// Callback that populates the environment-wide parameter set. Repeated calls merge,
    /// with the later call winning per key.
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentException">A parameter key is empty or whitespace.</exception>
    // [AspireExportIgnore]: the configure callback over a mutable dictionary is not
    // representable in the Aspire type system catalog (ASPIREEXPORT008); the method
    // is part of the public C# API surface and the export is suppressed only for ATS.
    [AspireExportIgnore(Reason = "The configure callback over a mutable dictionary is not representable in the ATS catalog (ASPIREEXPORT008).")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithRecipeParameters(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        Action<IDictionary<string, object>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var staging = new Dictionary<string, object>(StringComparer.Ordinal);
        configure(staging);

        var annotation = RadiusRecipeParametersAnnotation.GetOrAdd(builder.Resource);
        RadiusRecipeParametersAnnotation.Merge(annotation.EnvironmentWide, staging, nameof(configure));
        return builder;
    }

    /// <summary>
    /// Declares recipe parameters applied only to recipe entries of the given Radius
    /// resource type (e.g. <c>Radius.Data/redisCaches</c>). These take precedence over
    /// environment-wide parameters of the same key for that type.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="resourceType">The Radius resource type to scope the parameters to.</param>
    /// <param name="configure">
    /// Callback that populates the resource-type-scoped parameter set. Repeated calls for
    /// the same type merge, with the later call winning per key.
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceType"/> is empty/whitespace, or a parameter key is empty or whitespace.
    /// </exception>
    // [AspireExportIgnore]: see the environment-wide overload above (ASPIREEXPORT008).
    [AspireExportIgnore(Reason = "The configure callback over a mutable dictionary is not representable in the ATS catalog (ASPIREEXPORT008).")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithRecipeParameters(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        string resourceType,
        Action<IDictionary<string, object>> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentNullException.ThrowIfNull(configure);

        var staging = new Dictionary<string, object>(StringComparer.Ordinal);
        configure(staging);

        var annotation = RadiusRecipeParametersAnnotation.GetOrAdd(builder.Resource);
        if (!annotation.ByResourceType.TryGetValue(resourceType, out var target))
        {
            target = new Dictionary<string, object>(StringComparer.Ordinal);
            annotation.ByResourceType[resourceType] = target;
        }

        RadiusRecipeParametersAnnotation.Merge(target, staging, nameof(configure));
        return builder;
    }
}
