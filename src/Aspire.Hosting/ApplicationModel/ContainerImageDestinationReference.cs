// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the configured destination of an image artifact in a selected registry.
/// </summary>
/// <remarks>
/// The reference retains both artifact and registry provenance. Resolving the configured name
/// does not publish the image or assert that it exists at the destination.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class ContainerImageDestinationReference : IExpressionValue, IValueWithReferences
{
    internal ContainerImageDestinationReference(ContainerImageResource resource, IResource registry)
    {
        Resource = resource;
        Registry = registry;
    }

    /// <summary>
    /// Gets the source image artifact.
    /// </summary>
    public ContainerImageResource Resource { get; }

    /// <summary>
    /// Gets the explicitly selected destination registry resource.
    /// </summary>
    public IResource Registry { get; }

    /// <inheritdoc/>
    public string ValueExpression
    {
        get
        {
            Resource.GetPublication(Registry);
            return $"{{{Resource.Name}.publications.{Registry.Name}.image}}";
        }
    }

    /// <inheritdoc/>
    [AspireExportIgnore(Reason = "Reference enumeration is app-model dependency metadata.")]
    public IEnumerable<object> References => [Resource, Registry];

    /// <inheritdoc/>
    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var publication = Resource.GetPublication(Registry);
        var registry = (IContainerRegistry)publication.Registry;
        var endpoint = await registry.Endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        ContainerImageName.ValidateRegistry(endpoint ?? "", nameof(registry.Endpoint));

        var repository = publication.Repository;
        if (repository is null)
        {
            var registryRepository = registry.Repository is not null
                ? await registry.Repository.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                : null;
            repository = string.IsNullOrEmpty(registryRepository)
                ? Resource.Name.ToLowerInvariant()
                : $"{registryRepository}/{Resource.Name.ToLowerInvariant()}";
        }
        ContainerImageName.ValidateRepository(repository, nameof(registry.Repository));
        var name = $"{endpoint}/{repository}";
        if (name.Length > 255)
        {
            throw new InvalidOperationException($"Image artifact '{Resource.Name}' has a destination registry and repository exceeding 255 characters.");
        }

        return $"{name}:{publication.Tag}";
    }

    /// <inheritdoc/>
    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
    {
        return ((IValueProvider)this).GetValueAsync(new ValueProviderContext(), cancellationToken);
    }

    internal ReferenceExpression GetImageExpression()
    {
        var publication = Resource.GetPublication(Registry);
        var registry = (IContainerRegistry)publication.Registry;
        if (publication.Repository is not null)
        {
            return ReferenceExpression.Create($"{registry.Endpoint}/{publication.Repository}:{publication.Tag}");
        }
        if (registry.Repository is { ValueExpression.Length: > 0 })
        {
            return ReferenceExpression.Create($"{registry.Endpoint}/{registry.Repository}/{Resource.Name.ToLowerInvariant()}:{publication.Tag}");
        }

        return ReferenceExpression.Create($"{registry.Endpoint}/{Resource.Name.ToLowerInvariant()}:{publication.Tag}");
    }
}
