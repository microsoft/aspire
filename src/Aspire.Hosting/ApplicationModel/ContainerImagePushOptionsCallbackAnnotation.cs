// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that configures container image push options via a callback function.
/// </summary>
/// <remarks>
/// This annotation allows resources to customize how container images are named and tagged when pushed to a registry.
/// Multiple annotations can be added to a single resource, and all callbacks will be invoked in the order they were added.
/// Use <see cref="ResourceBuilderExtensions.WithImagePushOptions{T}(IResourceBuilder{T}, Action{ContainerImagePushOptionsCallbackContext})"/>
/// or <see cref="ResourceBuilderExtensions.WithImagePushOptions{T}(IResourceBuilder{T}, Func{ContainerImagePushOptionsCallbackContext, Task})"/>
/// to add this annotation to a resource.
/// </remarks>
[Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class ContainerImagePushOptionsCallbackAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImagePushOptionsCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The synchronous callback to configure push options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <c>null</c>.</exception>
    public ContainerImagePushOptionsCallbackAnnotation(Action<ContainerImagePushOptionsCallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = context =>
        {
            callback(context);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImagePushOptionsCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The asynchronous callback to configure push options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <c>null</c>.</exception>
    public ContainerImagePushOptionsCallbackAnnotation(Func<ContainerImagePushOptionsCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = callback;
    }

    /// <summary>
    /// Gets the callback function that configures image push options.
    /// </summary>
    /// <value>
    /// An asynchronous function that accepts a <see cref="ContainerImagePushOptionsCallbackContext"/> and modifies
    /// the <see cref="ContainerImagePushOptions"/> within it. The function is invoked when the image push options
    /// need to be resolved for the associated resource.
    /// </value>
    public Func<ContainerImagePushOptionsCallbackContext, Task> Callback { get; }
}
