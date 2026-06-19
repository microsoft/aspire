// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context for Dockerfile factory functions.
/// </summary>
[AspireExport]
public sealed class DockerfileFactoryContext
{
    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> for resolving dependencies required by Dockerfile factory functions.
    /// <para>
    /// The service provider typically contains services such as <c>IHostEnvironment</c>, <c>ILogger</c>, and configuration objects relevant to the application model.
    /// Factory functions can use this provider to obtain required services for generating Dockerfiles.
    /// </para>
    /// <example>
    /// <code>
    /// var logger = context.Services.GetRequiredService&lt;ILogger&gt;();
    /// </code>
    /// </example>
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Gets the resource for which the Dockerfile is being generated.
    /// <para>
    /// This allows factory functions to query resource annotations and properties to customize the generated Dockerfile.
    /// </para>
    /// <example>
    /// <code>
    /// var containerAnnotation = context.Resource.Annotations.OfType&lt;ContainerImageAnnotation&gt;().FirstOrDefault();
    /// var baseImage = containerAnnotation?.Image ?? "alpine:latest";
    /// </code>
    /// </example>
    /// </summary>
    [AspireExport]
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the cancellation token for the operation.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}
