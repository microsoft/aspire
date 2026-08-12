// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context information for Dockerfile build callbacks.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class DockerfileBuilderCallbackContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DockerfileBuilderCallbackContext"/> class.
    /// </summary>
    /// <param name="resource">The resource being built.</param>
    /// <param name="builder">The Dockerfile builder instance.</param>
    /// <param name="services">The service provider for dependency injection.</param>
    /// <param name="cancellationToken">The cancellation token to observe while waiting for the task to complete.</param>
    public DockerfileBuilderCallbackContext(IResource resource, DockerfileBuilder builder, IServiceProvider services, CancellationToken cancellationToken)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        Builder = builder ?? throw new ArgumentNullException(nameof(builder));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the resource being built.
    /// </summary>
    public IResource Resource { get; }

    /// <summary>
    /// Gets the Dockerfile builder instance.
    /// </summary>
    public DockerfileBuilder Builder { get; }

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the cancellation token to observe while waiting for the task to complete.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
