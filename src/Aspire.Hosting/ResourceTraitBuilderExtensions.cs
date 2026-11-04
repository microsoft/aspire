// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Provides methods for adding resource metadata and dashboard defaults.</summary>
public static class ResourceTraitBuilderExtensions
{
    /// <summary>Adds immutable, non-sensitive metadata to a resource. Repeated names add multiple values.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The case-sensitive trait name.</param>
    /// <param name="value">The trait value. Do not include secrets.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithTrait<T>(this IResourceBuilder<T> builder, string name, string value) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = new TraitAnnotation(name, value);

        if (name == KnownResourceTraits.DashboardPosition)
        {
            // Only the position trait is single-valued. Other traits deliberately accumulate.
            foreach (var previous in builder.Resource.Annotations.OfType<TraitAnnotation>().Where(t => t.Name == name).ToArray())
            {
                builder.Resource.Annotations.Remove(previous);
            }
        }

        return builder.WithAnnotation(annotation);
    }

    /// <summary>Adds AppHost-defined dashboard tags that users cannot remove. Repeated calls add tags.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tags">The tags, each up to 50 characters with single spaces between words.</param>
    /// <returns>The resource builder.</returns>
    [AspireExportIgnore(Reason = "ReadOnlySpan is not supported by ATS; use the withTags array adapter for polyglot SDKs.")]
    public static IResourceBuilder<T> WithTags<T>(this IResourceBuilder<T> builder, params ReadOnlySpan<string> tags) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var tag in tags)
        {
            builder.WithTrait(KnownResourceTraits.DashboardTag, tag);
        }

        return builder;
    }

    /// <summary>Adds AppHost-defined dashboard tags that users cannot remove. Repeated calls add tags.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tags">The tags, each up to 50 characters with single spaces between words.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withTags")]
    internal static IResourceBuilder<T> WithTagsForPolyglot<T>(this IResourceBuilder<T> builder, string[] tags) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(tags);

        return builder.WithTags(tags.AsSpan());
    }

    /// <summary>Sets the default resource position in the dashboard. Saved dashboard ordering takes precedence.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="position">The default position. Lower values appear first.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithDashboardPosition<T>(this IResourceBuilder<T> builder, int position) where T : IResource =>
        builder.WithTrait(KnownResourceTraits.DashboardPosition, position.ToString(CultureInfo.InvariantCulture));
}
