// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides contextual information for pipeline configuration callbacks.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public class PipelineConfigurationContext
{
    /// <summary>
    /// Gets the service provider for dependency resolution.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Gets the list of pipeline steps collected during the first pass.
    /// </summary>
    public required IReadOnlyList<PipelineStep> Steps
    {
        get;
        init
        {
            field = value;
            // IMPORTANT: The ResourceNameComparer must be used here to ensure correct lookup behavior
            // based on resource names, NOT the default reference equality. This is because resources
            // may be swapped out (referred to as bait-and-switch) during model transformations.
            StepToResourceMap = field.ToLookup(s => s.Resource, s => s, new ResourceNameComparer());
        }
    }

    /// <summary>
    /// Gets the distributed application model containing all resources.
    /// </summary>
    public required DistributedApplicationModel Model { get; init; }

    /// <summary>
    /// Gets the logger associated with the pipeline configuration pass.
    /// </summary>
    internal ILogger Logger { get; init; } = NullLogger.Instance;

    internal ILookup<IResource?, PipelineStep>? StepToResourceMap { get; init; }

    /// <summary>
    /// Gets the pipeline editor used by polyglot callbacks.
    /// </summary>
    [AspireExport(Description = "Gets the pipeline editor")]
    internal PipelineEditor Pipeline => new(Steps);

    /// <summary>
    /// Gets the logger facade used by polyglot callbacks.
    /// </summary>
    [AspireExport(Description = "Gets the callback logger facade")]
    internal LogFacade Log => new(Logger);

    /// <summary>
    /// Gets all pipeline steps with the specified tag.
    /// </summary>
    /// <param name="tag">The tag to search for.</param>
    /// <returns>A collection of steps that have the specified tag.</returns>
    [AspireExport(Description = "Gets pipeline steps with the specified tag")]
    public IEnumerable<PipelineStep> GetSteps(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return Steps.Where(s => s.Tags.Contains(tag));
    }

    /// <summary>
    /// Gets all pipeline steps associated with the specified resource.
    /// </summary>
    /// <param name="resource">The resource to search for.</param>
    /// <returns>A collection of steps associated with the resource.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use <see cref="Pipeline"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IResource parameters on callback context methods are not ATS-compatible. Use pipeline helpers instead.")]
    public IEnumerable<PipelineStep> GetSteps(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return StepToResourceMap?[resource] ?? [];
    }

    /// <summary>
    /// Gets all pipeline steps with the specified tag that are associated with the specified resource.
    /// </summary>
    /// <param name="resource">The resource to search for.</param>
    /// <param name="tag">The tag to search for.</param>
    /// <returns>A collection of steps that have the specified tag and are associated with the resource.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use <see cref="Pipeline"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IResource parameters on callback context methods are not ATS-compatible. Use pipeline helpers instead.")]
    public IEnumerable<PipelineStep> GetSteps(IResource resource, string tag)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(tag);
        return GetSteps(resource).Where(s => s.Tags.Contains(tag));
    }
}
