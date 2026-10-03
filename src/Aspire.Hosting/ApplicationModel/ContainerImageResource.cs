// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a container image artifact independently of a runnable container or application.
/// </summary>
/// <remarks>
/// The source is an existing registry image. Registry associations describe publication destinations;
/// constructing this resource does not pull, build, push, or run an image.
/// Without explicit associations, a single effective registry advertised by a compute environment
/// is adopted after environment preparation in publish mode.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class ContainerImageResource : Resource, IResourceWithoutLifetime
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageResource"/> class.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="image">The source image, including an optional tag, digest, or both.</param>
    /// <exception cref="ArgumentException">The source is not a valid container image reference.</exception>
    public ContainerImageResource(string name, string image) : base(name)
    {
        Source = ContainerImageName.ParseSource(image);
        SourceImage = $"{Source.Registry}/{Source.Image}" +
            (Source.Tag is not null ? $":{Source.Tag}" : "") +
            (Source.Digest is not null ? $"@{Source.Digest}" : "");
    }

    /// <summary>
    /// Gets the normalized source image reference, with a registry and repository.
    /// </summary>
    /// <remarks>
    /// Docker Hub shorthand is expanded, and an untagged source without a digest uses <c>latest</c>.
    /// Source tags are not resolved to digests during model construction.
    /// </remarks>
    public string SourceImage { get; }

    internal ContainerReference Source { get; }

    internal string DefaultTag
    {
        get
        {
            if (Source.Tag is not null)
            {
                return Source.Tag;
            }

            var tag = Source.Digest!.Replace(':', '-');
            // SHA-512 digests plus their algorithm prefix exceed the 128-character tag limit.
            return tag[..Math.Min(tag.Length, 128)];
        }
    }

    internal bool HasExplicitRegistryAssociation =>
        Annotations.OfType<ContainerImageRegistryAssociationAnnotation>().Any(a => a.DefaultEnvironment is null) ||
        this.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>();

    internal IReadOnlyList<ContainerImagePublicationAnnotation> GetPublications()
    {
        var hasExplicitAssociation = HasExplicitRegistryAssociation;
        var publications = Annotations.OfType<ContainerImagePublicationAnnotation>()
            .Where(p => !hasExplicitAssociation || p.DefaultEnvironment is null)
            .ToList();

        // WithContainerRegistry stays last-wins; WithPushedImage associations remain
        // additive. Explicit configuration immediately suppresses previously inferred destinations.
        if (this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var explicitRegistry))
        {
            if (explicitRegistry.Registry is not IResource registry)
            {
                throw new InvalidOperationException($"Image artifact '{Name}' requires a resource-backed container registry.");
            }

            if (!publications.Any(p => StringComparers.ResourceName.Equals(p.Registry.Name, registry.Name)))
            {
                publications.Add(new(registry, null, DefaultTag, defaultEnvironment: null));
            }
        }

        return publications;
    }

    internal ContainerImagePublicationAnnotation GetPublication(IResource registry)
    {
        return GetPublications().FirstOrDefault(p => StringComparers.ResourceName.Equals(p.Registry.Name, registry.Name))
            ?? throw new InvalidOperationException(
                $"Image artifact '{Name}' is not associated with registry '{registry.Name}'. Associate it with WithPushedImage or WithContainerRegistry first.");
    }
}
