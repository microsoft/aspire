// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the fully‑qualified container image reference that should be deployed.
/// </summary>
[DebuggerDisplay("{ValueExpression}")]
public class ContainerImageReference : IManifestExpressionProvider, IValueWithReferences, IValueProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageReference"/> class.
    /// </summary>
    /// <param name="resource">The resource that this container image is associated with.</param>
    public ContainerImageReference(IResource resource)
    {
        Resource = resource;
    }

    /// <summary>
    /// Gets the resource that this container image is associated with.
    /// </summary>
    public IResource Resource { get; }

    /// <inheritdoc/>
    public string ValueExpression => $"{{{Resource.Name}.containerImage}}";

    /// <inheritdoc/>
    public IEnumerable<object> References => [Resource];

    /// <inheritdoc/>
    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
    {
        return ((IValueProvider)this).GetValueAsync(new ValueProviderContext(), cancellationToken);
    }

    /// <inheritdoc/>
    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        // Check if this resource is configured to save as an archive instead of pushing to a registry
        if (context.ExecutionContext?.ServiceProvider is { } serviceProvider)
        {
            var logger = Resource.GetLogger(serviceProvider);
            var buildOptionsContext = await Resource.ProcessContainerBuildOptionsCallbackAsync(
                serviceProvider,
                logger,
                context.ExecutionContext,
                cancellationToken).ConfigureAwait(false);

            // For Archive destination, return the local file path
            if (buildOptionsContext.Destination == ContainerImageDestination.Archive)
            {
                if (!string.IsNullOrEmpty(buildOptionsContext.OutputPath))
                {
                    var imageName = buildOptionsContext.LocalImageName ?? Resource.Name.ToLowerInvariant();
                    var imageTag = buildOptionsContext.LocalImageTag;
                    return ResourceExtensions.GetContainerImageArchivePath(buildOptionsContext.OutputPath, imageName, imageTag);
                }
            }
        }

        return await Resource.GetFullRemoteImageNameAsync(cancellationToken).ConfigureAwait(false);
    }
}
