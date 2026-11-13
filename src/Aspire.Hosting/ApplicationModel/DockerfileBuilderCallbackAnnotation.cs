// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation for providing callbacks to programmatically modify Dockerfile builds.
/// </summary>
public class DockerfileBuilderCallbackAnnotation : IResourceAnnotation
{
    private readonly List<Func<DockerfileBuilderCallbackContext, Task>> _callbacks = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DockerfileBuilderCallbackAnnotation"/> class.
    /// </summary>
    public DockerfileBuilderCallbackAnnotation()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DockerfileBuilderCallbackAnnotation"/> class with an initial callback.
    /// </summary>
    /// <param name="callback">The initial callback function that will be invoked during the Dockerfile build process.</param>
    public DockerfileBuilderCallbackAnnotation(Func<DockerfileBuilderCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callbacks.Add(callback);
    }

    /// <summary>
    /// Gets the list of callback functions that will be invoked during the Dockerfile build process.
    /// </summary>
    public IReadOnlyList<Func<DockerfileBuilderCallbackContext, Task>> Callbacks => _callbacks.AsReadOnly();

    /// <summary>
    /// Adds a callback function to be invoked during the Dockerfile build process.
    /// </summary>
    /// <param name="callback">The callback function to add.</param>
    public void AddCallback(Func<DockerfileBuilderCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callbacks.Add(callback);
    }
}