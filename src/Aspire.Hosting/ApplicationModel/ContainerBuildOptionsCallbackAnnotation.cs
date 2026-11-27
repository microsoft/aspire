// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Annotation that provides a callback to configure container build options for a resource.
/// </summary>
/// <param name="callback">The callback function to configure container build options.</param>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerBuildOptionsCallbackAnnotation(Func<ContainerBuildOptionsCallbackContext, Task> callback) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback function that will be invoked to configure container build options.
    /// </summary>
    public Func<ContainerBuildOptionsCallbackContext, Task> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));

    /// <summary>
    /// Initializes a new instance of <see cref="ContainerBuildOptionsCallbackAnnotation"/> with a synchronous callback.
    /// </summary>
    /// <param name="callback">The synchronous callback action to configure container build options.</param>
    public ContainerBuildOptionsCallbackAnnotation(Action<ContainerBuildOptionsCallbackContext> callback)
        : this(context =>
        {
            callback(context);
            return Task.CompletedTask;
        })
    {
    }
}

/// <summary>
/// Context for configuring container build options via a callback.
/// </summary>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public sealed class ContainerBuildOptionsCallbackContext
{
    /// <summary>
    /// Initializes a new instance of <see cref="ContainerBuildOptionsCallbackContext"/>.
    /// </summary>
    /// <param name="resource">The resource being built.</param>
    /// <param name="services">The service provider.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="executionContext">The distributed application execution context.</param>
    public ContainerBuildOptionsCallbackContext(
        IResource resource,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken,
        DistributedApplicationExecutionContext executionContext)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken = cancellationToken;
        ExecutionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
    }

    /// <summary>
    /// Gets the resource being built.
    /// </summary>
    public IResource Resource { get; }

    /// <summary>
    /// Gets the service provider.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the distributed application execution context.
    /// </summary>
    /// <remarks>
    /// Use <see cref="DistributedApplicationExecutionContext.IsPublishMode"/> or
    /// <see cref="DistributedApplicationExecutionContext.IsRunMode"/> to vary build options
    /// (for example <see cref="TargetPlatform"/>) between local run and publish operations.
    /// </remarks>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>
    /// Gets or sets the destination for the container image.
    /// </summary>
    public ContainerImageDestination? Destination { get; set; }

    /// <summary>
    /// Gets or sets the output path for the container archive.
    /// </summary>
    /// <remarks>
    /// A non-existent path with any filename extension is an archive filename; otherwise the path is a
    /// directory that receives an archive named after the image. This applies to .NET SDK publishing and
    /// to Dockerfile builds. End a directory path with the platform's directory separator to make directory
    /// intent explicit, especially when its name contains a period. Prefer an explicit archive filename to
    /// avoid ambiguity.
    /// </remarks>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the container image format.
    /// </summary>
    public ContainerImageFormat? ImageFormat { get; set; }

    /// <summary>
    /// Gets or sets the target platform for the container.
    /// </summary>
    public ContainerTargetPlatform? TargetPlatform { get; set; }

    /// <summary>
    /// Gets or sets the local image name for the built container.
    /// </summary>
    /// <remarks>
    /// For Dockerfile builds, this name applies when the image is written to an archive
    /// (<see cref="OutputPath"/> is set). It names the built image, the archive file and the image reference
    /// recorded in the archive. Builds that load the image into the container runtime keep the resource's
    /// container image name, which deployment and push steps rely on.
    /// </remarks>
    public string? LocalImageName { get; set; }

    /// <summary>
    /// Gets or sets the local image tag for the built container.
    /// </summary>
    /// <remarks>
    /// For Dockerfile builds, this tag applies under the same conditions as <see cref="LocalImageName"/>.
    /// </remarks>
    public string? LocalImageTag { get; set; }
}
