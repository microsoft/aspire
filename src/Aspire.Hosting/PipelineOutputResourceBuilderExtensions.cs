// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring pipeline output directories.
/// </summary>
public static class PipelineOutputResourceBuilderExtensions
{
    /// <summary>
    /// Configures this resource's output directory when using the default pipeline output service.
    /// </summary>
    /// <typeparam name="TResource">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">A directory path relative to the global pipeline output directory, or a fully qualified directory path.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path is blank, invalid, or rooted without being fully qualified.</exception>
    /// <remarks>
    /// A later call replaces the previous override. Publishers must retrieve this directory through the resource-specific output lookup.
    /// This setting does not change temporary directories or redirect any file writes.
    /// Custom pipeline output services determine their own directory mappings.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddContainer("api", "myimage").WithPipelineOutputDirectory("deployment/api");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TResource> WithPipelineOutputDirectory<TResource>(
        this IResourceBuilder<TResource> builder,
        string path)
        where TResource : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new PipelineOutputDirectoryAnnotation(path), ResourceAnnotationMutationBehavior.Replace);
    }
}
