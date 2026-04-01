// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides extension methods for adding pipeline steps to resources.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class PipelineStepFactoryExtensions
{
    /// <summary>
    /// Adds a pipeline step to the resource that will be executed during deployment.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="factory">A factory function that creates the pipeline step.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the overload that takes a step name and callback instead.</remarks>
    [AspireExportIgnore(Reason = "Polyglot callbacks can't construct and return PipelineStep instances. Use the step-name overload instead.")]
    public static IResourceBuilder<T> WithPipelineStepFactory<T>(
        this IResourceBuilder<T> builder,
        Func<PipelineStepFactoryContext, PipelineStep> factory) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.WithAnnotation(new PipelineStepAnnotation(factory));
    }

    /// <summary>
    /// Adds a pipeline step to the resource that will be executed during deployment.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="factory">An async factory function that creates the pipeline step.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the overload that takes a step name and callback instead.</remarks>
    [AspireExportIgnore(Reason = "Polyglot callbacks can't construct and return PipelineStep instances. Use the step-name overload instead.")]
    public static IResourceBuilder<T> WithPipelineStepFactory<T>(
        this IResourceBuilder<T> builder,
        Func<PipelineStepFactoryContext, Task<PipelineStep>> factory) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.WithAnnotation(new PipelineStepAnnotation(factory));
    }

    /// <summary>
    /// Adds multiple pipeline steps to the resource that will be executed during deployment.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="factory">A factory function that creates multiple pipeline steps.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the overload that takes a step name and callback instead, and call it multiple times.</remarks>
    [AspireExportIgnore(Reason = "Polyglot callbacks can't construct and return PipelineStep instances. Use the step-name overload instead.")]
    public static IResourceBuilder<T> WithPipelineStepFactory<T>(
        this IResourceBuilder<T> builder,
        Func<PipelineStepFactoryContext, IEnumerable<PipelineStep>> factory) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.WithAnnotation(new PipelineStepAnnotation(factory));
    }

    /// <summary>
    /// Adds multiple pipeline steps to the resource that will be executed during deployment.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="factory">An async factory function that creates multiple pipeline steps.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the overload that takes a step name and callback instead, and call it multiple times.</remarks>
    [AspireExportIgnore(Reason = "Polyglot callbacks can't construct and return PipelineStep instances. Use the step-name overload instead.")]
    public static IResourceBuilder<T> WithPipelineStepFactory<T>(
        this IResourceBuilder<T> builder,
        Func<PipelineStepFactoryContext, Task<IEnumerable<PipelineStep>>> factory) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.WithAnnotation(new PipelineStepAnnotation(factory));
    }

    /// <summary>
    /// Adds a pipeline step to the resource that will be executed during deployment.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="stepName">The unique name of the pipeline step.</param>
    /// <param name="callback">The callback to execute when the step runs.</param>
    /// <param name="dependsOn">Optional step names that this step depends on.</param>
    /// <param name="requiredBy">Optional step names that require this step.</param>
    /// <param name="tags">Optional tags that categorize this step.</param>
    /// <param name="description">An optional human-readable description of the step.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport("withPipelineStepFactory", Description = "Adds a pipeline step to the resource")]
    public static IResourceBuilder<T> WithPipelineStepFactory<T>(
        this IResourceBuilder<T> builder,
        string stepName,
        Func<PipelineStepContext, Task> callback,
        string[]? dependsOn = null,
        string[]? requiredBy = null,
        string[]? tags = null,
        string? description = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = stepName,
            Description = description,
            Action = callback,
            DependsOnSteps = dependsOn is [..] ? [.. dependsOn] : [],
            RequiredBySteps = requiredBy is [..] ? [.. requiredBy] : [],
            Tags = tags is [..] ? [.. tags] : [],
            Resource = builder.Resource
        });
    }

    /// <summary>
    /// Registers a callback to be executed during the pipeline configuration phase,
    /// allowing modification of step dependencies and relationships.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback function to execute during the configuration phase.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the synchronous Action<> overload via withPipelineConfiguration.")]
    public static IResourceBuilder<T> WithPipelineConfiguration<T>(
        this IResourceBuilder<T> builder,
        Func<PipelineConfigurationContext, Task> callback) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new PipelineConfigurationAnnotation(callback));
    }

    /// <summary>
    /// Registers a callback to be executed during the pipeline configuration phase,
    /// allowing modification of step dependencies and relationships.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback function to execute during the configuration phase.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport("withPipelineConfiguration", Description = "Configures pipeline step dependencies via a callback")]
    public static IResourceBuilder<T> WithPipelineConfiguration<T>(
        this IResourceBuilder<T> builder,
        Action<PipelineConfigurationContext> callback) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new PipelineConfigurationAnnotation(callback));
    }
}
