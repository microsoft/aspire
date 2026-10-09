// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// An annotation that registers a callback to execute during the pipeline configuration phase,
/// allowing modification of step dependencies and relationships.
/// </summary>
public class PipelineConfigurationAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback function to execute during the configuration phase.
    /// </summary>
    public Func<PipelineConfigurationContext, Task> Callback { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineConfigurationAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The callback function to execute during the configuration phase.</param>
    public PipelineConfigurationAnnotation(Func<PipelineConfigurationContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Callback = callback;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineConfigurationAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The synchronous callback function to execute during the configuration phase.</param>
    public PipelineConfigurationAnnotation(Action<PipelineConfigurationContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Callback = (context) =>
        {
            callback(context);
            return Task.CompletedTask;
        };
    }
}
