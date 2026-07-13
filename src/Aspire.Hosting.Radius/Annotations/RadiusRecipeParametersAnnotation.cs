// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Annotations;

/// <summary>
/// Per-environment annotation that carries the recipe parameters declared via
/// <c>WithRecipeParameters</c>. Holds an environment-wide parameter set applied to
/// every recipe entry, plus parameter sets scoped to individual Radius resource
/// types. The annotation is per-resource and is never shared between environments
/// (FR-010); repeated declarations for the same scope merge with last-write-wins
/// per key (FR-016).
/// </summary>
internal sealed class RadiusRecipeParametersAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Parameters applied to every recipe entry in the environment's recipe pack.
    /// Ordinal-keyed.
    /// </summary>
    public Dictionary<string, object> EnvironmentWide { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Parameters scoped to a specific Radius resource type (e.g.
    /// <c>Radius.Data/redisCaches</c>). Outer key is the resource type string,
    /// forwarded verbatim.
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> ByResourceType { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the singleton <see cref="RadiusRecipeParametersAnnotation"/> on
    /// <paramref name="resource"/>, creating and attaching one if absent.
    /// </summary>
    internal static RadiusRecipeParametersAnnotation GetOrAdd(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var existing = resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        var created = new RadiusRecipeParametersAnnotation();
        resource.Annotations.Add(created);
        return created;
    }

    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="target"/> with
    /// last-write-wins per key (FR-016). Empty/whitespace keys are rejected (FR-009).
    /// </summary>
    /// <param name="target">The destination parameter set for the relevant scope.</param>
    /// <param name="source">The newly supplied parameters from a configure callback.</param>
    /// <param name="parameterName">Argument name used for thrown exceptions.</param>
    internal static void Merge(
        Dictionary<string, object> target,
        IDictionary<string, object> source,
        string parameterName)
    {
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "Recipe parameter keys must be non-empty and non-whitespace.",
                    parameterName);
            }

            target[key] = value;
        }
    }
}
