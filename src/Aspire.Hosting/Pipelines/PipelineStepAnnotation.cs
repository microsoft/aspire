// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// An annotation that creates pipeline steps for a resource during deployment.
/// </summary>
public class PipelineStepAnnotation : IResourceAnnotation
{
    private readonly Func<PipelineStepFactoryContext, Task<IEnumerable<PipelineStep>>> _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStepAnnotation"/> class.
    /// </summary>
    /// <param name="factory">A factory function that creates the pipeline step.</param>
    public PipelineStepAnnotation(Func<PipelineStepFactoryContext, PipelineStep> factory)
    {
        _factory = (context) => Task.FromResult<IEnumerable<PipelineStep>>([factory(context)]);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStepAnnotation"/> class.
    /// </summary>
    /// <param name="factory">An async factory function that creates the pipeline step.</param>
    public PipelineStepAnnotation(Func<PipelineStepFactoryContext, Task<PipelineStep>> factory)
    {
        _factory = async (context) => [await factory(context).ConfigureAwait(false)];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStepAnnotation"/> class with a factory that creates multiple pipeline steps.
    /// </summary>
    /// <param name="factory">A factory function that creates multiple pipeline steps.</param>
    public PipelineStepAnnotation(Func<PipelineStepFactoryContext, IEnumerable<PipelineStep>> factory)
    {
        _factory = (context) => Task.FromResult(factory(context));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStepAnnotation"/> class with a factory that creates multiple pipeline steps.
    /// </summary>
    /// <param name="factory">An async factory function that creates multiple pipeline steps.</param>
    public PipelineStepAnnotation(Func<PipelineStepFactoryContext, Task<IEnumerable<PipelineStep>>> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Creates pipeline steps asynchronously.
    /// </summary>
    /// <param name="context">The factory context containing the pipeline context and resource.</param>
    /// <returns>A task that represents the asynchronous operation and contains the created pipeline steps.</returns>
    public Task<IEnumerable<PipelineStep>> CreateStepsAsync(PipelineStepFactoryContext context) => _factory(context);
}
