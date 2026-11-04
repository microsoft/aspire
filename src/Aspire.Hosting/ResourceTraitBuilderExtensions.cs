// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        return builder.WithAnnotation(new TraitAnnotation(name, value));
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
            ArgumentException.ThrowIfNullOrWhiteSpace(tag, nameof(tags));

            if (tag.Length > 50 || tag != string.Join(' ', tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            {
                throw new ArgumentException("Dashboard tags must be at most 50 characters and use single spaces between words, without leading or trailing whitespace.", nameof(tags));
            }

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

    /// <summary>Sets the default position of the resource in lists of resources, such as the dashboard's. A saved ordering takes precedence. Repeated calls replace the position.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="position">The default position. Lower values appear first.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithDisplayPosition<T>(this IResourceBuilder<T> builder, int position) where T : IResource =>
        builder.WithAnnotation(new DisplayPositionAnnotation(position), ResourceAnnotationMutationBehavior.Replace);
}
