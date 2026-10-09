// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a container image artifact independently of a runnable container or application.
/// </summary>
/// <remarks>
/// Configure the source with <c>WithImageSource</c>, <c>WithDockerfile</c>, or <c>WithDockerfileBuilder</c> and create registry-scoped destinations with
/// <c>registry.AddImage(name, image)</c>. This resource has no compute or runtime lifetime.
/// Construction does not pull, build, push, or run an image.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class ContainerImageResource : Resource, IResourceWithoutLifetime
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageResource"/> class.
    /// </summary>
    /// <param name="name">The resource name.</param>
    public ContainerImageResource(string name) : base(name)
    {
    }

    internal ContainerImageSourceAnnotation GetSource()
    {
        if (!this.TryGetLastAnnotation<ContainerImageSourceAnnotation>(out var source))
        {
            throw new InvalidOperationException(
                $"Image artifact '{Name}' has no source. Configure it with WithImageSource, WithDockerfile, or WithDockerfileBuilder.");
        }
        if (source.Dockerfile is { } dockerfile &&
            (!this.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var active) || !ReferenceEquals(dockerfile, active)))
        {
            throw new InvalidOperationException($"Image artifact '{Name}' has a different Dockerfile build annotation than its configured source. Configure the source again.");
        }

        return source;
    }

    internal bool HasExplicitRegistryAssociation =>
        Annotations.OfType<ContainerImageRegistryAssociationAnnotation>().Any(a => a.DefaultEnvironment is null);

    internal IReadOnlyList<ContainerImagePublicationAnnotation> GetPublications()
    {
        var hasExplicitAssociation = HasExplicitRegistryAssociation;
        var publications = Annotations.OfType<ContainerImagePublicationAnnotation>()
            .Where(p => !hasExplicitAssociation || p.DefaultEnvironment is null)
            .ToList();

        return publications;
    }
}
