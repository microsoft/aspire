// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context information for container image push options callbacks.
/// </summary>
/// <remarks>
/// This context is passed to callbacks registered via <see cref="ResourceBuilderExtensions.WithImagePushOptions{T}(IResourceBuilder{T}, Action{ContainerImagePushOptionsCallbackContext})"/>.
/// Callbacks can use this context to access the resource being configured and modify the <see cref="Options"/>
/// to customize how the container image is named and tagged when pushed to a registry.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class ContainerImagePushOptionsCallbackContext
{
    /// <summary>
    /// Gets the resource being configured for container image push operations.
    /// </summary>
    /// <value>
    /// The resource instance that is being configured. This allows callbacks to access resource-specific
    /// information such as the resource name, annotations, or other metadata when determining image naming and tagging strategies.
    /// </value>
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the cancellation token to observe while configuring image push options.
    /// </summary>
    /// <value>
    /// The cancellation token that can be used to cancel asynchronous operations within the callback,
    /// such as retrieving configuration values or performing I/O operations.
    /// </value>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the container image push options that can be modified by the callback.
    /// </summary>
    /// <value>
    /// The <see cref="ContainerImagePushOptions"/> instance containing the remote image name and tag configuration.
    /// Callbacks should modify the <see cref="ContainerImagePushOptions.RemoteImageName"/> and
    /// <see cref="ContainerImagePushOptions.RemoteImageTag"/> properties to customize how the image is pushed to the registry.
    /// </value>
    public required ContainerImagePushOptions Options { get; init; }
}
