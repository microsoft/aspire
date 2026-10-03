// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for modeling container image artifacts and their registry destinations.
/// </summary>
public static class ContainerImageResourceBuilderExtensions
{
    /// <summary>
    /// Adds an existing container image as a standalone artifact.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The artifact resource name.</param>
    /// <param name="image">The source image, with an optional tag, digest, or both.</param>
    /// <returns>A builder for the image artifact.</returns>
    /// <remarks>
    /// The artifact is added to the application model only in publish mode. Registration does not
    /// pull, build, push, or run the image. Without explicit associations, the artifact adopts a single
    /// effective registry advertised by a compute environment after environment preparation.
    /// Multiple distinct environment defaults require an explicit association. Merely adding a
    /// generic registry does not opt the artifact into publication.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name or source image is invalid.</exception>
    /// <example>
    /// <code>
    /// var image = builder.AddContainerImage("tools", "ghcr.io/example/tools:v1");
    /// var registry = builder.AddContainerRegistry("registry", "registry.example.com");
    /// registry.WithPushedImage(image);
    /// var destination = image.GetImageReference(registry);
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<ContainerImageResource> AddContainerImage(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string image)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(image);

        var resource = new ContainerImageResource(name, image);
        var resourceBuilder = builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);

        return resourceBuilder.WithManifestPublishingCallback(context => WriteManifestAsync(context, resource));
    }

    /// <summary>
    /// Configures an image artifact to be pushed to a registry.
    /// </summary>
    /// <typeparam name="TRegistry">The registry resource type.</typeparam>
    /// <param name="builder">The registry builder.</param>
    /// <param name="image">The image artifact builder.</param>
    /// <param name="repository">The destination repository relative to the registry. Defaults to the registry namespace and lowercase artifact name.</param>
    /// <param name="tag">The destination tag. Defaults to the source tag or a digest-derived tag for a digest-only source.</param>
    /// <returns>The original registry builder.</returns>
    /// <remarks>
    /// Associations with different registries are additive. Identical repeated associations are
    /// idempotent; conflicting configuration for the same artifact and registry is rejected.
    /// An explicit repository replaces the registry namespace, never its endpoint.
    /// The registry endpoint must resolve to a hostname with an optional port, not a URL
    /// or an empty local-only endpoint. Explicit associations suppress automatic default-registry adoption.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required builder is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The repository or tag is invalid, or the builders belong to different applications.</exception>
    /// <exception cref="InvalidOperationException">An association with different configuration already exists.</exception>
    /// <example>
    /// <code>
    /// firstRegistry.WithPushedImage(image);
    /// secondRegistry.WithPushedImage(image, repository: "tools/helper", tag: "release");
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withRegistryPushedImage", MethodName = "withPushedImage")]
    public static IResourceBuilder<TRegistry> WithPushedImage<TRegistry>(
        this IResourceBuilder<TRegistry> builder,
        IResourceBuilder<ContainerImageResource> image,
        string? repository = null,
        string? tag = null)
        where TRegistry : IResource, IContainerRegistry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);
        if (repository is not null)
        {
            ContainerImageName.ValidateRepository(repository, nameof(repository));
        }
        if (tag is not null)
        {
            ContainerImageName.ValidateTag(tag, nameof(tag));
        }

        tag ??= image.Resource.DefaultTag;
        IResource registryResource = builder.Resource;
        var existing = image.Resource.Annotations.OfType<ContainerImagePublicationAnnotation>()
            .FirstOrDefault(p => p.DefaultEnvironment is null &&
                StringComparers.ResourceName.Equals(p.Registry.Name, registryResource.Name));
        if (existing is not null)
        {
            if (!string.Equals(existing.Repository, repository, StringComparison.Ordinal) ||
                !string.Equals(existing.Tag, tag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Image artifact '{image.Resource.Name}' already has different publication settings for registry '{registryResource.Name}'.");
            }

            return builder;
        }

        image.WithAnnotation(new ContainerImagePublicationAnnotation(builder.Resource, repository, tag, defaultEnvironment: null));

        return builder;
    }

    /// <summary>
    /// Gets a structured image reference for an associated registry.
    /// </summary>
    /// <typeparam name="TRegistry">The registry resource type.</typeparam>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="registry">The selected registry builder.</param>
    /// <returns>A destination reference retaining the image and registry.</returns>
    /// <exception cref="ArgumentNullException">A required builder is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The builders belong to different applications.</exception>
    /// <exception cref="InvalidOperationException">The image has no association with the registry.</exception>
    /// <remarks>
    /// A registry must be selected even when the artifact currently has only one destination.
    /// The reference can be composed with other structured values without resolving registry parameters.
    /// Automatically adopted associations are available after environment preparation.
    /// </remarks>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static ContainerImageDestinationReference GetImageReference<TRegistry>(
        this IResourceBuilder<ContainerImageResource> builder, IResourceBuilder<TRegistry> registry)
        where TRegistry : IResource, IContainerRegistry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);
        ValidateApplication(builder.ApplicationBuilder, registry.ApplicationBuilder);
        builder.Resource.GetPublication(registry.Resource);

        return new(builder.Resource, registry.Resource);
    }

    private static void ValidateApplication(IDistributedApplicationBuilder imageBuilder, IDistributedApplicationBuilder registryBuilder)
    {
        if (!ReferenceEquals(imageBuilder, registryBuilder))
        {
            throw new ArgumentException("The image and registry must belong to the same distributed application.");
        }
    }

    private static Task WriteManifestAsync(ManifestPublishingContext context, ContainerImageResource resource)
    {
        context.Writer.WriteString("type", "containerimage.v0");
        context.Writer.WriteString("source", resource.SourceImage);
        context.Writer.WriteStartObject("publications");
        foreach (var publication in resource.GetPublications().OrderBy(p => p.Registry.Name, StringComparers.ResourceName))
        {
            var reference = new ContainerImageDestinationReference(resource, publication.Registry);
            var expression = reference.GetImageExpression();
            context.Writer.WriteStartObject(publication.Registry.Name);
            context.Writer.WriteString("registry", publication.Registry.Name);
            context.Writer.WriteString("image", expression.ValueExpression);
            context.TryAddDependentResources(expression);
            context.Writer.WriteEndObject();
        }
        context.Writer.WriteEndObject();

        return Task.CompletedTask;
    }
}
